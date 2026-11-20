using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Tomlyn;
using Tomlyn.Model;

namespace AnketaParser.Infrastructure.Llm;

public enum LlmEngine { OpenAi, LlamaServer, Native }
public enum LlmResponseMode { Text, JsonObject, JsonSchema, LlamaSchema }

public sealed record LlmSettings(
    string Name, LlmEngine Engine, string Model, Uri? Endpoint, string? KeyVariable,
    IReadOnlyDictionary<string, string> HeaderVariables, LlmResponseMode ResponseMode, string TokenParameter,
    string? ReasoningEffort, string? ModelPath, int ContextTokens, int Threads, int GpuLayers,
    int MaxOutputTokens, int MaxInputChars, float? Temperature, int TimeoutSeconds, int Concurrency,
    int? DailyRequestLimit, string BudgetKey, string PipelineVersion, bool ReasoningAsObject = false)
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

public sealed class LlmConfigurationException(string category) : Exception(category)
{
    public string Category { get; } = category;
}

public static class LlmConfiguration
{
    private const int MaxFileBytes = 64 * 1024;

    public static async Task<LlmSettings?> LoadAsync(IConfiguration configuration, string workingDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredPath = configuration["Llm:ConfigPath"];
        var activeOverride = configuration["Llm:ActiveProfile"];
        var explicitPath = !string.IsNullOrWhiteSpace(configuredPath);
        try
        {
            var path = Path.GetFullPath(explicitPath ? configuredPath! : "llm.toml", workingDirectory);
            TomlTable table;
            try
            {
                await using var stream = File.OpenRead(path);
                if (stream.Length > MaxFileBytes)
                    throw Invalid("toml_too_large");
                var bytes = new byte[MaxFileBytes + 1];
                var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
                if (read > MaxFileBytes)
                    throw Invalid("toml_too_large");
                var content = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, read).TrimStart('\uFEFF');
                table = TomlSerializer.Deserialize<TomlTable>(content) ?? throw Invalid("invalid_toml");
            }
            catch (FileNotFoundException) when (!explicitPath && string.IsNullOrEmpty(activeOverride))
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                throw Invalid("toml_missing");
            }
            catch (DirectoryNotFoundException)
            {
                throw Invalid("toml_missing");
            }
            catch (Exception error) when (error is TomlException or System.Text.DecoderFallbackException)
            {
                throw Invalid("invalid_toml");
            }

            RequireKeys(table, "active_profile", "profiles");
            var active = activeOverride ?? Read<string>(table, "active_profile");
            if (string.IsNullOrEmpty(active))
                return null;
            if (!IsIdentifier(active) || Read<TomlTable>(table, "profiles") is not { } profiles ||
                Read<TomlTable>(profiles, active) is not { } profile)
                throw Invalid("profile_missing");
            return await ParseAsync(active, profile, Path.GetDirectoryName(path)!, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Invalid("toml_unreadable");
        }
    }

    private static async Task<LlmSettings> ParseAsync(string active, TomlTable profile, string directory, CancellationToken ct)
    {
        var engine = Read<string>(profile, "engine") switch
        {
            "openai" => LlmEngine.OpenAi,
            "llama_server" => LlmEngine.LlamaServer,
            "native" => LlmEngine.Native,
            _ => throw Invalid("invalid_engine"),
        };
        var native = engine == LlmEngine.Native;
        string[] common = ["engine", "model", "timeout_seconds", "concurrency", "max_output_tokens", "max_input_chars", "temperature"];
        RequireKeys(profile, native
            ? [.. common, "gguf_path", "context_tokens", "threads", "gpu_layers"]
            : [.. common, "base_url", "key_env", "headers_env", "response_mode", "token_parameter", "reasoning_effort", "reasoning_format", "daily_request_limit"]);

        var model = Read<string>(profile, "model");
        if (string.IsNullOrWhiteSpace(model) || model.Length > 160 || model.Any(char.IsControl))
            throw Invalid("invalid_model");
        var timeout = Integer(profile, "timeout_seconds", native ? 180 : 60, 1, 3600);
        var concurrency = Integer(profile, "concurrency", 1, 1, native ? 1 : 16);
        var maxOutput = Integer(profile, "max_output_tokens", 768, 64, 4096);
        var maxInput = Integer(profile, "max_input_chars", 12000, 64, 32768);
        var temperature = Number(profile, "temperature", native ? 0.1f : null, 0, 2);
        var context = Integer(profile, "context_tokens", 8192, 1024, 131072);
        var threads = Integer(profile, "threads", 0, 0, 256);
        var layers = Integer(profile, "gpu_layers", 0, 0, 999);
        var response = Read<string>(profile, "response_mode") switch
        {
            null or "text" => LlmResponseMode.Text,
            "json_object" => LlmResponseMode.JsonObject,
            "json_schema" => LlmResponseMode.JsonSchema,
            "llama_schema" when engine == LlmEngine.LlamaServer => LlmResponseMode.LlamaSchema,
            _ => throw Invalid("invalid_response_mode"),
        };
        var tokenParameter = Read<string>(profile, "token_parameter") ?? "max_tokens";
        if (tokenParameter is not ("max_tokens" or "max_completion_tokens") ||
            (engine == LlmEngine.LlamaServer && tokenParameter != "max_tokens"))
            throw Invalid("invalid_token_parameter");
        var reasoning = Read<string>(profile, "reasoning_effort");
        if (reasoning is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh") ||
            engine == LlmEngine.LlamaServer && reasoning is not null)
            throw Invalid("invalid_reasoning_effort");

        var reasoningAsObject = Read<string>(profile, "reasoning_format") switch
        {
            null => false,
            "object" when engine == LlmEngine.OpenAi && reasoning is "none" or "low" or "medium" or "high" => true,
            _ => throw Invalid("invalid_reasoning_format"),
        };

        var key = Read<string>(profile, "key_env");
        var headerVariables = ReadHeaders(profile);
        Uri? endpoint = null;
        string? modelPath = null;
        string? modelHash = null;
        if (native)
        {
            if (context <= maxOutput)
                throw Invalid("invalid_context_tokens");
            (modelPath, modelHash) = await ReadModelAsync(Read<string>(profile, "gguf_path"), directory, ct).ConfigureAwait(false);
        }
        else
        {
            if (!Uri.TryCreate(Read<string>(profile, "base_url"), UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != "https" && (baseUri.Scheme != "http" || engine == LlmEngine.OpenAi && !baseUri.IsLoopback)) ||
                baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 ||
                baseUri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                throw Invalid("invalid_base_url");
            endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
            EnsureSecretTransport(endpoint, key, headerVariables);
            if (key is not null)
                _ = ResolveSecret(key);
        }

        var budget = profile.ContainsKey("daily_request_limit")
            ? Integer(profile, "daily_request_limit", 0, 2, 1_000_000)
            : (int?)null;
        // Хеш бюджета объединяет профили одного маршрута, даже если сменились промт или параметры.
        var budgetKey = Fingerprint(new { endpoint = endpoint?.AbsoluteUri, key });
        var version = "llm-" + Fingerprint(new
        {
            contract = SemanticContract.Fingerprint,
            engine,
            model,
            modelHash,
            endpoint = endpoint?.AbsoluteUri,
            response,
            tokenParameter,
            reasoning,
            reasoningAsObject,
            context,
            threads,
            layers,
            maxOutput,
            maxInput,
            temperature,
            headers = headerVariables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
        });
        return new(active, engine, model, endpoint, key, headerVariables, response, tokenParameter, reasoning, modelPath,
            context, threads, layers, maxOutput, maxInput, temperature, timeout, concurrency, budget, budgetKey, version, reasoningAsObject);
    }

    private static async Task<(string Path, string Hash)> ReadModelAsync(string? file, string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw Invalid("model_missing");
        try
        {
            var path = Path.GetFullPath(file, directory);
            await using var stream = File.OpenRead(path);
            var magic = new byte[4];
            if (await stream.ReadAtLeastAsync(magic, magic.Length, false, ct).ConfigureAwait(false) != magic.Length ||
                !magic.AsSpan().SequenceEqual("GGUF"u8))
                throw Invalid("invalid_gguf");
            stream.Position = 0;
            return (path, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Invalid("model_unavailable");
        }
    }

    internal static void EnsureSecretTransport(Uri endpoint, string? key, IReadOnlyDictionary<string, string> headers)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback && (key is not null || headers.Count != 0))
            throw Invalid("insecure_secret_transport");
    }

    internal static string ResolveSecret(string variable)
    {
        if (!IsIdentifier(variable))
            throw Invalid("invalid_secret_variable");
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw Invalid("secret_unavailable");
        return value;
    }

    private static Dictionary<string, string> ReadHeaders(TomlTable profile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Read<TomlTable>(profile, "headers_env") is not { } headers)
            return result;
        foreach (var (name, value) in headers)
        {
            if (name.Length is 0 or > 80 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                value is not string variable || !result.TryAdd(name, variable) || result.Count > 16)
                throw Invalid("invalid_headers");
            _ = ResolveSecret(variable);
        }
        return result;
    }

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 96 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void RequireKeys(TomlTable table, params string[] names)
    {
        if (table.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
            throw Invalid("unsupported_toml_key");
    }

    private static T? Read<T>(TomlTable table, string key) where T : class =>
        !table.TryGetValue(key, out var value) ? null : value as T ?? throw Invalid("invalid_profile_value");

    private static int Integer(TomlTable table, string key, int fallback, int min, int max) =>
        !table.TryGetValue(key, out var value) ? fallback :
        value is long number && number >= min && number <= max ? (int)number : throw Invalid("invalid_profile_value");

    private static float? Number(TomlTable table, string key, float? fallback, float min, float max)
    {
        if (!table.TryGetValue(key, out var value))
            return fallback;
        var number = value switch { long integer => integer, double fraction => fraction, _ => double.NaN };
        return number >= min && number <= max ? (float)number : throw Invalid("invalid_profile_value");
    }

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static LlmConfigurationException Invalid(string category) => new(category);

    public static string EngineName(LlmEngine engine) => engine switch
    {
        LlmEngine.OpenAi => "openai",
        LlmEngine.LlamaServer => "llama_server",
        LlmEngine.Native => "native",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };
}