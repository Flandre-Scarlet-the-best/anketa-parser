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
            var inputTokens = weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
            if (inputTokens + settings.MaxOutputTokens >= settings.ContextTokens)
                throw new LlmFailureException("context_limit");

            var executor = new StatelessExecutor(weights, model) { ApplyTemplate = false };
            using var initialContext = executor.Context;
            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = settings.Temperature.GetValueOrDefault(),
                Grammar = new Grammar(request.Grammar, "root"),
            };
            var parameters = new InferenceParams { MaxTokens = settings.MaxOutputTokens, SamplingPipeline = sampler };
            var output = new StringBuilder();
            var pieces = 0;
            await foreach (var fragment in executor.InferAsync(prompt, parameters, cancellationToken).ConfigureAwait(false))
            {
                pieces++;
                if (output.Length + fragment.Length > SemanticContract.MaxResponseChars)
                    throw new LlmFailureException("output_limit");
                output.Append(fragment);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pieces >= settings.MaxOutputTokens || output.Length == 0)
                throw new LlmFailureException("incomplete_output");
            return new(output.ToString(), new(inputTokens, pieces, null));
        }
        catch (Exception error) when (error is RuntimeError or LLamaDecodeError or MissingTemplateException or TemplateNotFoundException or
            ContextOverflowException or InvalidOperationException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new LlmFailureException("native_inference_failed");
        }
    }

    public void Dispose() => weights.Dispose();
}