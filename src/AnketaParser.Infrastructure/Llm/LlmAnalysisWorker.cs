using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Llm;

public sealed record LlmWorkOutcome(string Status, Guid? RevisionId = null, int? Attempt = null, TimeSpan? RetryAfter = null);
internal sealed record AttemptMetrics(long ElapsedMilliseconds, int MeasuredRequests, long? InputTokens, long? OutputTokens, long? CachedInputTokens);

public sealed class LlmAnalysisWorker(DbContextOptions<ProductDbContext> database, LlmSettings settings, ILlmEngine engine)
{
    public const int MaxRequestsPerAttempt = 2;

    public async Task<LlmWorkOutcome> ProcessNextAsync(Guid? revisionId, CancellationToken cancellationToken)
    {
        await using var context = new ProductDbContext(database);
        var store = new ProductStore(context);
        // Каждая из двух операций имеет свой таймаут. Запас покрывает чтение,
        // транзакцию завершения и отмену; просроченная аренда проверяется при записи.
        var leaseDuration = settings.Timeout * MaxRequestsPerAttempt + TimeSpan.FromSeconds(30);
        var lease = await store.ClaimAsync(leaseDuration, settings, revisionId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            var limit = settings.DailyRequestLimit;
            if (limit is not null && await store.ReservedTodayAsync(settings, cancellationToken).ConfigureAwait(false) >
                limit - MaxRequestsPerAttempt)
                return new("budget_exhausted");
            return new("idle");
        }
        var started = Stopwatch.StartNew();
        SemanticResult? result = null;
        LlmFailureException? failure = null;
        List<LlmUsage> usage = [];
        try
        {
            var text = await store.ReadLlmTextAsync(lease, settings.MaxInputChars, cancellationToken).ConfigureAwait(false);
            if (text is null)
                throw new LlmFailureException("revision_ineligible");
            var decision = await store.ReadCheckpointAsync(lease, text, cancellationToken).ConfigureAwait(false);
            if (decision is null)
            {
                var classified = await GenerateAsync(SemanticOperation.Classify, text, usage, cancellationToken).ConfigureAwait(false);
                decision = SemanticContract.ReadDecision(classified, text);
            }
            SemanticExtraction? extraction = null;
            if (decision.Class == SemanticContract.Profile)
            {
                // Промежуточное решение не является Analysis или Candidate и сохраняется
                // только под действующей арендой и при актуальной ревизии.
                if (!await store.SaveCheckpointAsync(lease, settings, decision, text, cancellationToken).ConfigureAwait(false))
                    throw new LlmFailureException("revision_ineligible");
                // Если пост изменён во время первого вызова, второй запрос больше не нужен.
                if (await store.ReadLlmTextAsync(lease, settings.MaxInputChars, cancellationToken).ConfigureAwait(false) is not { } current ||
                    !string.Equals(current, text, StringComparison.Ordinal))
                    throw new LlmFailureException("revision_ineligible");
                var extracted = await GenerateAsync(SemanticOperation.Extract, text, usage, cancellationToken).ConfigureAwait(false);
                extraction = SemanticContract.ReadFacts(extracted, text);
            }
            result = new(decision, extraction);
        }
        catch (LlmFailureException error)
        {
            // Провайдер мог израсходовать токены и всё равно прислать отказ:
            // сохраняем его статистику, не превращая ответ в успешный результат.
            if (error.Usage is { } measured)
                usage.Add(measured);
            failure = error;
        }
        catch (LlmConfigurationException error)
        {
            failure = new LlmFailureException(error.Category);
        }
        catch (Exception error) when (error is JsonException or RegexMatchTimeoutException)
        {
            failure = new LlmFailureException("source_text_invalid");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new LlmFailureException("cancelled", retryable: true);
        }

        var metrics = new AttemptMetrics(started.ElapsedMilliseconds, usage.Count,
            usage.Count > 0 ? usage.Sum(x => (long)x.Input) : null,
            usage.Count > 0 ? usage.Sum(x => (long)x.Output) : null,
            usage.Any(x => x.CachedInput.HasValue) ? usage.Sum(x => (long?)x.CachedInput) : null);
        // При штатной остановке даём хранилищу короткое время закрыть попытку.
        // При остановке процесса без завершения аренду освобождает следующая выдача.
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await store.FinishLlmAsync(lease, settings, result, failure, metrics, finish.Token).ConfigureAwait(false);
        return new(outcome, lease.RevisionId, lease.Attempt, failure?.RetryAfter);
    }

    private async Task<string> GenerateAsync(SemanticOperation operation, string text, List<LlmUsage> usage, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            var result = await engine.GenerateAsync(SemanticContract.Request(operation, text), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (result.Usage is { } measured)
                usage.Add(measured);
            return result.Text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmFailureException("timeout", retryable: true);
        }
    }
}