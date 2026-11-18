using System.Text.Json;

using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Persistence;

internal sealed class DvachScanStore(ProductDbContext db)
{
    private readonly ProductStore _product = new(db);

    internal async Task<bool> TryLockAsync(string key, CancellationToken ct)
    {
        // Закрытие выделенного непулируемого соединения освобождает блокировку даже при отмене.
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await db.Database.SqlQuery<bool>($"""
            SELECT pg_try_advisory_lock(hashtextextended({"dvach:scan:" + key}, 0)) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
    }

    internal async Task<SourceScan> LoadAsync(string key, string url, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var source = await _product.EnsureSourceAsync(key, url, ct).ConfigureAwait(false);
        var state = await db.Set<SourceScan>().FindAsync([source.Id], ct).ConfigureAwait(false);
        if (state is null)
        {
            state = new SourceScan { SourceId = source.Id };
            db.Add(state);
            await SaveAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return state;
    }

    internal async Task ApplyAsync(SourceScan state, PreparedPost input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await _product.LockSourceAsync(input.Post.Source, ct).ConfigureAwait(false);

        var (post, revision, newPost, newRevision) = await _product.ImportPostAsync(state.SourceId, input.Post, ct).ConfigureAwait(false);
        var changed = !newPost && (post.IngestedRevisionId is { } previous ? previous != revision.Id : newRevision);
        post.IngestedRevisionId = revision.Id;
        var needsPreparation = revision.IngestionReason is null;
        if (needsPreparation)
        {
            revision.IngestionReason = input.Reason;
            revision.IngestionReply = input.Reply;
            revision.IngestionContacts = input.Contacts.Count;
            revision.IngestionAmbiguous = input.Ambiguous;
            revision.RetrievalMetadata = input.Post.RetrievalMetadata;
            // Упорядоченные ключи контактов позволяют разным тредам применять ревизии параллельно.
            foreach (var contact in input.Contacts.OrderBy(c => c.Platform, StringComparer.Ordinal)
                         .ThenBy(c => c.NormalizedValue, StringComparer.Ordinal))
            {
                await _product.RegisterObservationAsync(new ContactInput(revision.Id, contact.Platform, contact.RawValue,
                    contact.NormalizedValue, contact.Confidence, contact.Provenance), ct).ConfigureAwait(false);
            }
        }

        var reason = post.Excluded ? DvachFilter.Excluded : revision.IngestionReason;
        if (needsPreparation && reason == DvachFilter.Candidate)
        {
            await SaveAsync(ct).ConfigureAwait(false);
            await _product.EnqueueAsync(revision.Id, DvachFilter.ProcessorVersion, DvachFilter.MaxAttempts, ct).ConfigureAwait(false);
        }
        ++state.Applied;
        var metrics = ReadMetrics(state);
        state.Metrics = JsonSerializer.Serialize(metrics with
        {
            NewPosts = metrics.NewPosts + (newPost ? 1 : 0),
            ChangedPosts = metrics.ChangedPosts + (changed ? 1 : 0),
            RepeatedPosts = metrics.RepeatedPosts + (!newPost && !changed ? 1 : 0),
            Candidates = metrics.Candidates + (reason == DvachFilter.Candidate ? 1 : 0),
            Ambiguous = metrics.Ambiguous + (reason == DvachFilter.Candidate && revision.IngestionAmbiguous ? 1 : 0),
            Contacts = metrics.Contacts + revision.IngestionContacts,
            OpeningPosts = metrics.OpeningPosts + (reason == "opening_post" ? 1 : 0),
            OtherReplies = metrics.OtherReplies + (reason == "reply_to_other_post" ? 1 : 0),
            NoOwnContact = metrics.NoOwnContact + (reason == "no_own_contact" ? 1 : 0),
            Excluded = metrics.Excluded + (reason == DvachFilter.Excluded ? 1 : 0),
        });
        await SaveAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.Entity != state).ToArray())
            entry.State = EntityState.Detached;
    }

    internal async Task SaveAsync(CancellationToken ct) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

    internal async Task<DvachScanReport> StatusAsync(string key, CancellationToken ct)
    {
        var scan = await (from state in db.Set<SourceScan>().AsNoTracking()
                          join source in db.Set<Source>() on state.SourceId equals source.Id
                          where source.Key == key
                          select state).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return scan is null ? Report(new SourceScan()) : Report(scan);
    }

    internal async Task<DvachPostReport> PostAsync(string key, long postNumber, CancellationToken ct)
    {
        var id = DvachSnapshot.PostId(postNumber);
        var report = await (from post in db.Set<OriginalPost>()
                            join source in db.Set<Source>() on post.SourceId equals source.Id
                            join rev in db.Set<PostRevision>() on post.IngestedRevisionId equals rev.Id
                            where source.Key == key && post.ExternalId == id
                            select new DvachPostReport("ok", post.Excluded ? DvachFilter.Excluded : rev.IngestionReason,
                                rev.IngestionReply, db.Set<PostRevision>().Count(r => r.PostId == post.Id),
                                rev.IngestionContacts, rev.IngestionAmbiguous,
                                db.Set<ProcessingJob>().Where(j => j.RevisionId == rev.Id && j.ProcessorVersion == DvachFilter.ProcessorVersion)
                                    .Select(j => j.State).FirstOrDefault())).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return report ?? new DvachPostReport("post_not_found");
    }

    internal async Task<IReadOnlyList<DvachQueueCount>> QueueAsync(string key, CancellationToken ct) =>
        await (from job in db.Set<ProcessingJob>()
               join rev in db.Set<PostRevision>() on job.RevisionId equals rev.Id
               join post in db.Set<OriginalPost>() on rev.PostId equals post.Id
               join source in db.Set<Source>() on post.SourceId equals source.Id
               where source.Key == key && job.ProcessorVersion == DvachFilter.ProcessorVersion
               group job by new { job.State, Current = !post.Excluded && post.IngestedRevisionId == rev.Id }
               into jobs
               orderby jobs.Key.State, jobs.Key.Current
               select new DvachQueueCount(jobs.Key.State, jobs.Key.Current, jobs.Count())).ToListAsync(ct).ConfigureAwait(false);

    internal static DvachScanMetrics ReadMetrics(SourceScan state) =>
        JsonSerializer.Deserialize<DvachScanMetrics>(state.Metrics) ?? new();

    internal static DvachScanReport Report(SourceScan state) => new(
        state.Status, state.Phase, state.Failure, state.HttpStatus, state.StartedAt, state.FinishedAt,
        state.LastSuccessAt, state.RetryAt, state.ConsecutiveFailures, state.Pending is not null, state.Applied,
        state.StartedAt is null ? null : ReadMetrics(state));

    internal async Task<DvachScanReport> RecoverFailureAsync(SourceScan state, DateTimeOffset now,
        int pollInterval, string category, string phase, int? status, DateTimeOffset? retryAt, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.Set<SourceScan>().Where(x => x.SourceId == state.SourceId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.Phase, phase)
                .SetProperty(x => x.Failure, category)
                .SetProperty(x => x.HttpStatus, status)
                .SetProperty(x => x.ConsecutiveFailures, x => Math.Min(10, x.ConsecutiveFailures + 1))
                .SetProperty(x => x.RetryAt, Backoff(now, pollInterval, state.ConsecutiveFailures, retryAt)), ct)
            .ConfigureAwait(false);
        return Report(await db.Set<SourceScan>().AsNoTracking().SingleAsync(x => x.SourceId == state.SourceId, ct).ConfigureAwait(false));
    }

    internal async Task MarkInterruptedAsync(Guid sourceId, DateTimeOffset now, CancellationToken ct) =>
        await db.Set<SourceScan>().Where(x => x.SourceId == sourceId && x.Status == "running")
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "interrupted")
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.Failure, "cancelled"), ct).ConfigureAwait(false);

    private static DateTimeOffset Backoff(DateTimeOffset now, int interval, int failures, DateTimeOffset? retryAt)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(86400, interval * (1 << Math.Min(5, failures))));
        var backoff = now + delay;
        return retryAt > backoff ? retryAt.Value : backoff;
    }
}