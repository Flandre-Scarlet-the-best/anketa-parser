using System.Text;

using AnketaParser.Infrastructure.Llm;

using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace AnketaParser.Cli;

internal sealed class NativeLlmEngine(LLamaWeights weights, ModelParams model, LlmSettings settings) : ILlmEngine, IDisposable
{
    private static readonly object NativeSettingsLock = new();
    private static int? _gpuLayers;

    internal static async Task<NativeLlmEngine> LoadAsync(LlmSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            lock (NativeSettingsLock)
            {
                if (_gpuLayers is null)
                {
                    NativeLibraryConfig.All.WithLogCallback(static (_, _) => { })
                        .WithCuda(settings.GpuLayers > 0).WithVulkan(settings.GpuLayers > 0)
                        .WithAutoFallback(settings.GpuLayers == 0);
                    _gpuLayers = settings.GpuLayers;
                }
                else if (_gpuLayers != settings.GpuLayers)
                {
                    throw new LlmFailureException("native_restart_required");
                }
            }
            var model = new ModelParams(settings.ModelPath!)
            {
                ContextSize = (uint)settings.ContextTokens,
                Threads = settings.Threads == 0 ? null : settings.Threads,
                BatchThreads = settings.Threads == 0 ? null : settings.Threads,
                GpuLayerCount = settings.GpuLayers,
            };
            var weights = await LLamaWeights.LoadFromFileAsync(model, timeout.Token).ConfigureAwait(false);
            if (timeout.IsCancellationRequested)
            {
                weights.Dispose();
                timeout.Token.ThrowIfCancellationRequested();
            }
            return new(weights, model, settings);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmFailureException("model_load_timeout");
        }
        catch (Exception error) when (error is RuntimeError or LoadWeightsFailedException or DllNotFoundException or BadImageFormatException or
            EntryPointNotFoundException or TypeInitializationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ct.ThrowIfCancellationRequested();
            throw new LlmFailureException(timeout.IsCancellationRequested ? "model_load_timeout" : "native_model_unavailable");
        }
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var template = new LLamaTemplate(weights) { AddAssistant = true };
            template.Add("system", request.System).Add("user", request.User);
            var prompt = Encoding.UTF8.GetString(template.Apply());
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = weights.Tokenize(prompt, true, true, Encoding.UTF8);
            var inputTokens = tokens.Length;
            if (inputTokens + settings.MaxOutputTokens >= settings.ContextTokens)
                throw new LlmFailureException("context_limit");

            using var context = weights.CreateContext(model);
            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = settings.Temperature.GetValueOrDefault(),
                Grammar = new Grammar(request.Grammar, "root"),
            };
            sampler.Reset();
            var decoder = new StreamingTokenDecoder(context);
            var batch = new LLamaBatch();
            // StatelessExecutor выполняет prefill целиком без проверки отмены.
            // Ожидаем каждый native-вызов до освобождения контекста и весов.
            var chunkSize = Math.Min(64, checked((int)context.BatchSize));
            await DecodePromptAsync(tokens.Length, chunkSize, async (offset, count, ct) =>
            {
                batch.Clear();
                batch.AddRange(tokens.AsSpan(offset, count), offset, LLamaSeqId.Zero, offset + count == tokens.Length);
                var result = await context.DecodeAsync(batch, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result != DecodeResult.Ok)
                    throw new LLamaDecodeError(result);
            }, cancellationToken).ConfigureAwait(false);

            var output = new StringBuilder();
            var pieces = 0;
            var position = inputTokens;
            while (pieces < settings.MaxOutputTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = sampler.Sample(context.NativeHandle, batch.TokenCount - 1);
                if (token.IsEndOfGeneration(weights.Vocab))
                    break;
                decoder.Add(token);
                var fragment = decoder.Read();
                pieces++;
                if (output.Length + fragment.Length > SemanticContract.MaxResponseChars)
                    throw new LlmFailureException("output_limit");
                output.Append(fragment);
                batch.Clear();
                batch.Add(token, position++, LLamaSeqId.Zero, true);
                var result = await context.DecodeAsync(batch, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result != DecodeResult.Ok)
                    throw new LLamaDecodeError(result);
            }
            if (pieces >= settings.MaxOutputTokens || output.Length == 0)
                // Неполный ответ не принимается, но уже потраченные токены учитываются.
                throw new LlmFailureException("incomplete_output", usage: new LlmUsage(inputTokens, pieces, null));
            return new(output.ToString(), new(inputTokens, pieces, null));
        }
        catch (Exception error) when (error is RuntimeError or LLamaDecodeError or MissingTemplateException or TemplateNotFoundException or
            ContextOverflowException or InvalidOperationException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new LlmFailureException("native_inference_failed");
        }
    }

    internal static async Task DecodePromptAsync(int tokenCount, int chunkSize,
        Func<int, int, CancellationToken, Task> decode, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        for (var offset = 0; offset < tokenCount; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await decode(offset, Math.Min(chunkSize, tokenCount - offset), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose() => weights.Dispose();
}