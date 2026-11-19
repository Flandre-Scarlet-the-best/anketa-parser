using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Persistence;

public sealed partial class ProductStore
{
    private static readonly JsonSerializerOptions LlmJson = new(JsonSerializerDefaults.Web);

    internal async Task<bool> CanClaimLlmAsync(LlmSettings settings, CancellationToken ct)
    {
        await LockAsync("llm-version:" + settings.PipelineVersion, ct).ConfigureAwait(false);
        var now = Now;
        var running = await db.Set<ProcessingJob>().CountAsync(job => job.LlmVersion == settings.PipelineVersion &&
            job.State == "running" && job.LeaseUntil > now, ct).ConfigureAwait(false);
        if (running >= settings.Concurrency)
            return false;
        if (settings.Endpoint is null)
            return true;

        await LockAsync("llm-budget:" + settings.BudgetKey, ct).ConfigureAwait(false);
        var limit = settings.DailyRequestLimit;
        return limit is null ||
            await ReservedTodayAsync(settings, ct).ConfigureAwait(false) <= limit - LlmAnalysisWorker.MaxRequestsPerAttempt;
    }

    internal Task<long> ReservedTodayAsync(LlmSettings settings, CancellationToken ct)
    {
        var start = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        return db.Set<JobAttempt>().Where(attempt => attempt.BudgetKey == settings.BudgetKey && attempt.StartedAt >= start)
            .SumAsync(attempt => (long)attempt.ReservedRequests, ct);
    }

    internal async Task<Guid?> QueueLlmAsync(Guid revisionId, LlmSettings settings, bool retryFailed, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var revision = await db.Set<PostRevision>().SingleOrDefaultAsync(x => x.Id == revisionId, ct).ConfigureAwait(false);
        if (revision is null)
            return null;
        await LockPostAsync(revision.PostId, ct).ConfigureAwait(false);
        if (!await IsLlmEligibleAsync(revision.Id, ct).ConfigureAwait(false))
            return null;

        var existing = await db.Set<ProcessingJob>().SingleOrDefaultAsync(x =>
            x.RevisionId == revision.Id && x.LlmVersion == settings.PipelineVersion, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (retryFailed && existing.State == "failed" && existing.Attempts < 100)
            {
                // Порядок блокировок совпадает с завершением и захватом задания.
                await LockJobAsync(existing.Id, ct).ConfigureAwait(false);
                if (existing.State == "failed")
                {
                    existing.MaxAttempts = Math.Min(100, existing.Attempts + DvachFilter.MaxAttempts);
                    existing.State = "pending";
                    existing.AvailableAt = Now;
                    existing.FailureCategory = null;
                }
            }
        }
        else
        {
            var original = await db.Set<ProcessingJob>().SingleOrDefaultAsync(x => x.RevisionId == revision.Id &&
                x.ProcessorVersion == DvachFilter.ProcessorVersion && x.LlmVersion == null && x.State == "pending", ct).ConfigureAwait(false);
            var id = original?.Id ??
                await EnqueueAsync(revision.Id, settings.PipelineVersion, DvachFilter.MaxAttempts, ct).ConfigureAwait(false);
            existing = await LockJobAsync(id, ct).ConfigureAwait(false);
            existing.LlmVersion = settings.PipelineVersion;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return existing.Id;
    }

    internal async Task<string?> ReadLlmTextAsync(JobLease lease, int maxInputChars, CancellationToken ct)
    {
        var now = Now;
        var metadata = await (from job in db.Set<ProcessingJob>()
                              join revision in db.Set<PostRevision>() on job.RevisionId equals revision.Id
                              join post in db.Set<OriginalPost>() on revision.PostId equals post.Id
                              where job.Id == lease.JobId && job.RevisionId == lease.RevisionId &&
                                  job.LeaseToken == lease.Token && job.LeaseUntil > now && job.State == "running" &&
                                  !post.Excluded && post.IngestedRevisionId == revision.Id && revision.IngestionReason == DvachFilter.Candidate
                              select revision.RetrievalMetadata).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (metadata is null)
            return null;

        using var document = JsonDocument.Parse(metadata);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("text", out var item) || item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { } text)
            throw new LlmFailureException("source_text_unavailable");
        if (text.Length > maxInputChars)
            throw new LlmFailureException("input_limit");
        var contacts = await (from observation in db.Set<ContactObservation>()
                              join contact in db.Set<Contact>() on observation.ContactId equals contact.Id
                              where observation.RevisionId == lease.RevisionId
                              select new { observation.RawValue, contact.NormalizedValue })
            .Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        return LlmInput.Redact(text, contacts.SelectMany(x => new[] { x.RawValue, x.NormalizedValue }));
    }

    internal sealed record ClassificationCheckpoint(string InputHash, SemanticDecision Decision);

    private static string InputHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal async Task<SemanticDecision?> ReadCheckpointAsync(JobLease lease, string text, CancellationToken ct)
    {
        var json = await db.Set<JobAttempt>().AsNoTracking()
            .Where(attempt => attempt.JobId == lease.JobId && attempt.Number < lease.Attempt && attempt.Classification != null)
            .OrderByDescending(attempt => attempt.Number)
            .Select(attempt => attempt.Classification)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var checkpoint = json is null ? null : JsonSerializer.Deserialize<ClassificationCheckpoint>(json, LlmJson);
        return checkpoint?.InputHash == InputHash(text) ? checkpoint.Decision : null;
    }

    internal async Task<bool> SaveCheckpointAsync(JobLease lease, LlmSettings settings, SemanticDecision decision, string text, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var postId = await db.Set<PostRevision>().Where(revision => revision.Id == lease.RevisionId)
            .Select(revision => revision.PostId).SingleAsync(ct).ConfigureAwait(false);
        await LockPostAsync(postId, ct).ConfigureAwait(false);
        var job = await LockJobAsync(lease.JobId, ct).ConfigureAwait(false);
        if (job.RevisionId != lease.RevisionId || job.LlmVersion != settings.PipelineVersion ||
            job.State != "running" || job.LeaseToken != lease.Token || job.LeaseUntil is not { } expiresAt || expiresAt <= Now ||
            !await IsLlmEligibleAsync(lease.RevisionId, ct).ConfigureAwait(false))
            return false;
        var attempt = await db.Set<JobAttempt>().SingleAsync(x => x.Token == lease.Token, ct).ConfigureAwait(false);
        attempt.Classification = JsonSerializer.Serialize(new ClassificationCheckpoint(InputHash(text), decision), LlmJson);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Аренда может истечь, пока выполнялась запись: транзакция тогда откатывается.
        if (expiresAt <= Now)
            return false;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal async Task<string> FinishLlmAsync(JobLease lease, LlmSettings settings, SemanticResult? result, LlmFailureException? failure,
        AttemptMetrics metrics, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var postId = await db.Set<PostRevision>().Where(revision => revision.Id == lease.RevisionId)
            .Select(revision => revision.PostId).SingleAsync(ct).ConfigureAwait(false);
        await LockPostAsync(postId, ct).ConfigureAwait(false);
        var job = await LockJobAsync(lease.JobId, ct).ConfigureAwait(false);
        var now = Now;
        if (job.RevisionId != lease.RevisionId || job.LlmVersion != settings.PipelineVersion ||
            job.State != "running" || job.LeaseToken != lease.Token || job.LeaseUntil is not { } expiresAt || expiresAt <= now)
            return "lease_lost";
        if (!await IsLlmEligibleAsync(lease.RevisionId, ct).ConfigureAwait(false))
        {
            result = null;
            failure = new LlmFailureException("revision_ineligible");
        }

        if (result is not null)
        {
            var analysis = ToAnalysis(job, settings, result, now);
            db.Add(analysis);
            if (result.Decision.Class == SemanticContract.Profile)
                db.Add(new Profile { AnalysisId = analysis.Id, PostId = postId, CreatedAt = now, State = ProfileState.Candidate });
        }

        var canRetry = failure is { Retryable: true } && job.Attempts < job.MaxAttempts;
        job.State = failure is null ? "succeeded" : canRetry ? "pending" : "failed";
        job.LeaseToken = null;
        job.LeaseUntil = null;
        job.FailureCategory = failure?.Category;
        job.AvailableAt = canRetry ? now + RetryDelay(job.Attempts, failure!.RetryAfter) : now;
        var attempt = await db.Set<JobAttempt>().SingleAsync(x => x.Token == lease.Token, ct).ConfigureAwait(false);
        attempt.FinishedAt = now;
        attempt.FailureCategory = failure?.Category;
        attempt.ElapsedMilliseconds = metrics.ElapsedMilliseconds;
        attempt.MeasuredRequests = metrics.MeasuredRequests;
        attempt.InputTokens = metrics.InputTokens;
        attempt.OutputTokens = metrics.OutputTokens;
        attempt.CachedInputTokens = metrics.CachedInputTokens;
        if (expiresAt <= Now)
            return "lease_lost";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result?.Decision.Class ?? failure!.Category;
    }

    private async Task<ProcessingJob> LockJobAsync(Guid id, CancellationToken ct)
    {
        var jobs = await db.Set<ProcessingJob>().FromSqlInterpolated(
            $"""SELECT * FROM product."ProcessingJob" WHERE "Id"={id} FOR UPDATE""").ToListAsync(ct).ConfigureAwait(false);
        var job = jobs.Single();
        await db.Entry(job).ReloadAsync(ct).ConfigureAwait(false);
        return job;
    }

    private Task<bool> IsLlmEligibleAsync(Guid revisionId, CancellationToken ct) =>
        (from revision in db.Set<PostRevision>()
         join post in db.Set<OriginalPost>() on revision.PostId equals post.Id
         where revision.Id == revisionId && !post.Excluded && post.IngestedRevisionId == revisionId &&
             revision.IngestionReason == DvachFilter.Candidate
         select revision.Id).AnyAsync(ct);

    private static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter)
    {
        var backoff = TimeSpan.FromSeconds(10 * (1 << Math.Min(attempt - 1, 5)));
        return retryAfter > backoff ? TimeSpan.FromSeconds(Math.Min(retryAfter.Value.TotalSeconds, 86400)) : backoff;
    }

    private static Analysis ToAnalysis(ProcessingJob job, LlmSettings settings, SemanticResult result, DateTimeOffset now)
    {
        string? Value(string key) => result.Extraction?.Facts.GetValueOrDefault(key)?.Value;
        return new Analysis
        {
            RevisionId = job.RevisionId,
            ProcessingJobId = job.Id,
            Processor = LlmConfiguration.EngineName(settings.Engine),
            PipelineVersion = settings.PipelineVersion,
            Model = settings.Model,
            SchemaVersion = SemanticContract.SchemaVersion,
            SemanticClass = result.Decision.Class,
            SemanticReason = result.Decision.Reason,
            Age = Value("age") is { } age ? int.Parse(age, CultureInfo.InvariantCulture) : null,
            Location = Value("location"),
            GenderDescription = Value("gender"),
            Interests = Value("interests"),
            Goals = Value("goals"),
            Observations = JsonSerializer.Serialize(new
            {
                fields = result.Extraction?.Facts.ToDictionary(x => x.Key, x => x.Value.Evidence, StringComparer.Ordinal),
                unsupportedFields = result.Extraction?.UnsupportedFields,
                preferences = Value("preferences"),
                description = Value("description"),
            }, LlmJson),
            Assessments = JsonSerializer.Serialize(new { evidence = result.Decision.Evidence, evidenceOmitted = result.Decision.EvidenceOmitted }, LlmJson),
            CreatedAt = now,
        };
    }
}