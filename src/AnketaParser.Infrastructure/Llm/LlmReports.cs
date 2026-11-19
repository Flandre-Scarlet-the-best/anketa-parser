using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Llm;

public sealed class LlmReports(ProductDbContext database)
{
    public async Task<object> StatusAsync(LlmSettings? settings, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var version = settings?.PipelineVersion;
        var queue = await (from job in database.Set<ProcessingJob>().AsNoTracking()
                           join revision in database.Set<PostRevision>() on job.RevisionId equals revision.Id
                           join post in database.Set<OriginalPost>() on revision.PostId equals post.Id
                           where job.ProcessorVersion == DvachFilter.ProcessorVersion || job.LlmVersion != null
                           group job by new
                           {
                               job.State,
                               Current = !post.Excluded && post.IngestedRevisionId == revision.Id &&
                                   revision.IngestionReason == DvachFilter.Candidate,
                               Active = version != null && (job.LlmVersion == null || job.LlmVersion == version),
                               Expired = job.LeaseUntil != null && job.LeaseUntil <= now,
                           }
                into groupRows
                           select new { groupRows.Key.State, groupRows.Key.Current, groupRows.Key.Active, groupRows.Key.Expired, Count = groupRows.Count() })
            .ToArrayAsync(ct).ConfigureAwait(false);
        var decisions = await database.Set<Analysis>().Where(x => x.SemanticClass != null)
            .GroupBy(x => x.SemanticClass).Select(g => new { Class = g.Key, Count = g.Count() }).ToArrayAsync(ct).ConfigureAwait(false);
        var attempts = from attempt in database.Set<JobAttempt>().AsNoTracking()
                       join job in database.Set<ProcessingJob>() on attempt.JobId equals job.Id
                       where job.LlmVersion != null
                       select new { attempt, job.LlmVersion };
        var failures = await attempts.Where(x => x.attempt.FailureCategory != null)
            .GroupBy(x => x.attempt.FailureCategory).Select(g => new { Category = g.Key, Count = g.Count() }).ToArrayAsync(ct).ConfigureAwait(false);
        var metrics = await attempts.GroupBy(_ => 1).Select(g => new
        {
            Attempts = g.Count(),
            MeanMilliseconds = g.Average(x => (double?)x.attempt.ElapsedMilliseconds),
            InputTokens = g.Count(x => x.attempt.InputTokens != null) == 0 ? (long?)null : g.Sum(x => x.attempt.InputTokens),
            OutputTokens = g.Count(x => x.attempt.OutputTokens != null) == 0 ? (long?)null : g.Sum(x => x.attempt.OutputTokens),
            CachedInputTokens = g.Count(x => x.attempt.CachedInputTokens != null) == 0 ? (long?)null : g.Sum(x => x.attempt.CachedInputTokens),
            MeasuredRequests = g.Sum(x => (long)x.attempt.MeasuredRequests),
        }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var last = version is null ? null : await attempts
            .Where(x => x.LlmVersion == version && x.attempt.FinishedAt != null)
            .OrderByDescending(x => x.attempt.FinishedAt)
            .Select(x => new { x.attempt.FinishedAt, x.attempt.FailureCategory })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var reservedToday = settings?.Endpoint is null ? (long?)null :
            await new ProductStore(database).ReservedTodayAsync(settings, ct).ConfigureAwait(false);
        var engineObservation = last is null ? "unobserved" : last.FailureCategory is null ? "responded" : "attempt_failed";
        return new
        {
            Queue = queue,
            Decisions = decisions,
            Failures = failures,
            Metrics = metrics,
            EngineObservation = engineObservation,
            LastAttempt = last,
            ReservedToday = reservedToday
        };
    }

    public async Task<object?> DecisionAsync(Guid revisionId, CancellationToken ct)
    {
        var revision = await (from item in database.Set<PostRevision>()
                              join post in database.Set<OriginalPost>() on item.PostId equals post.Id
                              where item.Id == revisionId
                              select new
                              {
                                  PostNumber = post.ExternalId,
                                  Current = post.IngestedRevisionId == item.Id && !post.Excluded && item.IngestionReason == DvachFilter.Candidate,
                              }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (revision is null)
            return null;
        var results = await (from analysis in database.Set<Analysis>().AsNoTracking()
                             where analysis.RevisionId == revisionId && analysis.SemanticClass != null
                             orderby analysis.CreatedAt descending, analysis.Id
                             select new
                             {
                                 AnalysisId = analysis.Id,
                                 Class = analysis.SemanticClass,
                                 Reason = analysis.SemanticReason,
                                 Engine = analysis.Processor,
                                 analysis.Model,
                                 analysis.PipelineVersion,
                                 analysis.SchemaVersion,
                                 analysis.CreatedAt,
                                 ProfileId = database.Set<Profile>().Where(p => p.AnalysisId == analysis.Id).Select(p => (Guid?)p.Id).FirstOrDefault(),
                             }).Take(100).ToArrayAsync(ct).ConfigureAwait(false);
        var jobs = await database.Set<ProcessingJob>().AsNoTracking()
            .Where(x => x.RevisionId == revisionId && (x.LlmVersion != null || x.ProcessorVersion == DvachFilter.ProcessorVersion))
            .OrderByDescending(x => x.AvailableAt).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.State, x.LlmVersion, x.Attempts, x.MaxAttempts, x.AvailableAt, x.FailureCategory })
            .Take(100).ToArrayAsync(ct).ConfigureAwait(false);
        return new { RevisionId = revisionId, revision.PostNumber, revision.Current, Results = results, Jobs = jobs };
    }

    public Task<Guid?> ReprocessAsync(Guid revisionId, LlmSettings settings, bool retryFailed, CancellationToken ct) =>
        new ProductStore(database).QueueLlmAsync(revisionId, settings, retryFailed, ct);
}
