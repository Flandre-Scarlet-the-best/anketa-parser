using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnketaParser.Infrastructure.Llm;

public sealed class OpenAiLlmEngine(LlmSettings settings, HttpClient client) : ILlmEngine
{
    private const int MaxResponseBytes = 256 * 1024;

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmConfiguration.EnsureSecretTransport(settings.Endpoint!, settings.KeyVariable, settings.HeaderVariables);
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = new JsonArray(Message("system", request.System), Message("user", request.User)),
            ["stream"] = false,
            [settings.TokenParameter] = settings.MaxOutputTokens,
        };
        if (settings.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (settings.ReasoningEffort is not null)
            body["reasoning_effort"] = settings.ReasoningEffort;
        if (Format(settings.ResponseMode, request) is { } format)
            body["response_format"] = format;
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint) { Content = JsonContent.Create(body) };
        if (settings.KeyVariable is not null &&
            !message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + LlmConfiguration.ResolveSecret(settings.KeyVariable)))
            throw new LlmConfigurationException("invalid_headers");
        foreach (var (name, variable) in settings.HeaderVariables)
            if (!message.Headers.TryAddWithoutValidation(name, LlmConfiguration.ResolveSecret(variable)))
                throw new LlmConfigurationException("invalid_headers");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ReadFailureAsync(response, timeout.Token).ConfigureAwait(false);
            using var document = await ReadAsync(response, timeout.Token).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmFailureException("timeout", retryable: true);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new LlmFailureException("network_unavailable", retryable: true);
        }
        catch (JsonException)
        {
            throw new LlmFailureException("invalid_provider_json", retryable: true);
        }
    }

    private static JsonObject Message(string role, string value) => new() { ["role"] = role, ["content"] = value };

    private static JsonObject? Format(LlmResponseMode mode, LlmRequest request) => mode switch
    {
        LlmResponseMode.Text => null,
        LlmResponseMode.JsonObject => new JsonObject { ["type"] = "json_object" },
        LlmResponseMode.JsonSchema => new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = request.Operation == SemanticOperation.Classify ? "profile_classification" : "profile_extraction",
                ["strict"] = true,
                ["schema"] = request.Schema.DeepClone(),
            },
        },
        LlmResponseMode.LlamaSchema => new JsonObject { ["type"] = "json_object", ["schema"] = request.Schema.DeepClone() },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new LlmFailureException("output_limit");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false);
        if (count > MaxResponseBytes)
            throw new LlmFailureException("output_limit");
        return JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
    }

    private static async Task<LlmFailureException> ReadFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is 429 or 503)
            return new(status == 429 ? "rate_limited" : "provider_unavailable", true, RetryDelay(response.Headers.RetryAfter));
        if (status >= 500 || status == 408)
            return new("provider_unavailable", true);
        if (status is 400 or 422)
        {
            try
            {
                using var document = await ReadAsync(response, ct).ConfigureAwait(false);
                if (String(Property(Property(document.RootElement, "error"), "code")) is
                    "context_length_exceeded" or "context_window_exceeded" or "prompt_too_long")
                    return new("context_limit");
            }
            catch (Exception error) when (error is JsonException or LlmFailureException)
            {
                // Не возвращаем содержимое ошибки провайдера.
            }
        }
        return new(status switch
        {
            401 or 403 => "provider_auth",
            402 => "provider_payment_required",
            404 => "model_or_endpoint_unavailable",
            405 => "endpoint_method_not_allowed",
            400 or 422 => "unsupported_request",
            _ => "provider_rejected",
        });
    }

    private static TimeSpan? RetryDelay(RetryConditionHeaderValue? retryAfter) =>
        retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow;

    private static LlmResponse Parse(JsonElement root)
    {
        // Статистику читаем до проверок формы ответа: провайдер уже израсходовал токены,
        // и отказ не должен скрывать этот расход.
        var usage = ReadUsage(root);
        var choices = Property(root, "choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw new LlmFailureException("unsupported_response", usage: usage);
        var choice = choices[0];
        var finish = String(Property(choice, "finish_reason"));
        var message = Property(choice, "message");
        if (finish == "length")
            throw new LlmFailureException("incomplete_output", usage: usage);
        if (finish == "content_filter" || String(Property(message, "refusal")) is not null)
            throw new LlmFailureException("model_refusal", usage: usage);
        if (finish != "stop" || Property(message, "tool_calls").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ||
            Property(message, "function_call").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ||
            String(Property(message, "content")) is not { Length: > 0 } text)
            throw new LlmFailureException("unsupported_response", usage: usage);
        return new(text, usage);
    }

    private static LlmUsage? ReadUsage(JsonElement root)
    {
        var usage = Property(root, "usage");
        var input = Count(Property(usage, "prompt_tokens"));
        var output = Count(Property(usage, "completion_tokens"));
        var cached = Count(Property(Property(usage, "prompt_tokens_details"), "cached_tokens"));
        return input is { } i && output is { } o ? new(i, o, cached <= i ? cached : null) : null;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Count(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;
}