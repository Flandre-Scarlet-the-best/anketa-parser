using System.Buffers;
using System.Net;
using System.Text.Json;

namespace AnketaParser.Infrastructure.Sources;

internal sealed class DvachClient(HttpClient client, TimeProvider clock)
{
    internal async Task<DvachSnapshot> FetchAsync(DvachThread thread, DvachSettings settings, string? fixturePath, CancellationToken ct)
    {
        var phase = "fetch";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        try
        {
            var payload = fixturePath is null
                ? await FetchHttpAsync(thread, settings.MaxResponseBytes, deadline.Token).ConfigureAwait(false)
                : await FetchFileAsync(fixturePath, settings.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            phase = "parse";
            deadline.Token.ThrowIfCancellationRequested();
            return DvachSnapshot.Parse(payload, thread);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SourceFailure("timeout", phase);
        }
        catch (HttpRequestException)
        {
            throw new SourceFailure("transport_error", phase);
        }
        catch (IOException)
        {
            throw new SourceFailure(fixturePath is null ? "transport_error" : "fixture_unavailable", phase);
        }
        catch (UnauthorizedAccessException) when (fixturePath is not null)
        {
            throw new SourceFailure("fixture_unavailable", phase);
        }
        catch (JsonException)
        {
            throw new SourceFailure("invalid_json", phase);
        }
    }

    private async Task<byte[]> FetchHttpAsync(DvachThread thread, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, thread.JsonUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("anketa-parser (+https://github.com/Flandre-Scarlet-the-best/anketa-parser)");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } delay ? clock.GetUtcNow() + delay : (DateTimeOffset?)null);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var category = response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Gone => "thread_unavailable",
                HttpStatusCode.TooManyRequests => "rate_limited",
                HttpStatusCode.PartialContent => "incomplete_response",
                _ => "http_error",
            };
            throw new SourceFailure(category, "fetch", status, retryAt);
        }
        var contentType = response.Content.Headers.ContentType;
        if (response.Content.Headers.ContentRange is not null)
            throw new SourceFailure("incomplete_response", "fetch", status);
        if (contentType?.MediaType != "application/json" ||
            (contentType.CharSet is { } charset && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new SourceFailure("unexpected_content_type", "fetch", status);
        if (response.Content.Headers.ContentLength > limit)
            throw new SourceFailure("response_too_large", "fetch", status);

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await ReadAsync(input, limit, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && payload.Length != length)
            throw new SourceFailure("incomplete_response", "fetch", status);
        return payload;
    }

    private static async Task<byte[]> FetchFileAsync(string path, int limit, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (input.Length > limit)
            throw new SourceFailure("response_too_large", "fetch");
        return await ReadAsync(input, limit, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAsync(Stream input, int limit, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                if (data.Length + count > limit)
                    throw new SourceFailure("response_too_large", "fetch");
                data.Write(buffer, 0, count);
            }
            return data.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}