using System.Globalization;
using System.Text.Json;

using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Только чтение существующей схемы продукта: снимок одного скана, решения первичного фильтра,
/// задания, попытки, решения модели и контакты. Запросы не изменяют данные и не вызывают модель.
/// </summary>
public sealed class ReportQueries(ProductDbContext database)
{
    /// <summary>Предел числа постов вне снимка, попадающих в отчёт.</summary>
    private const int MaxOutsidePosts = 200;

    /// <summary>Читает факты снимка источника по ключу.</summary>
    /// <param name="sourceKey">Значение <c>Source.Key</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Факты снимка; проверки предусловий выполняет вызывающая сторона.</returns>
    /// <exception cref="ReportDiagnosticException">Источник или скан отсутствуют в хранилище.</exception>
    public async Task<ReportSnapshot> LoadAsync(string sourceKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var source = await database.Set<Source>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == sourceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new ReportDiagnosticException("source_not_found", [new ReportDiagnosticFact("sourceKey", sourceKey)]);
        var scan = await database.Set<SourceScan>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.SourceId == source.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new ReportDiagnosticException("scan_not_found", [new ReportDiagnosticFact("sourceKey", sourceKey)]);

        var (posts, outsidePosts, idsInWindow, outsideCount) = await LoadPostsAsync(source.Id, scan, cancellationToken)
            .ConfigureAwait(false);
        var scanFacts = ToScanFacts(scan) with { IdsInWindow = idsInWindow, PostsOutsideWindow = outsideCount };
        var sourceFacts = new ReportSourceFacts(source.Id, source.Key, source.Url);
        if (posts.Count == 0)
            return new ReportSnapshot(sourceFacts, scanFacts, [], await LoadOutsideOnlyAsync(outsidePosts, cancellationToken)
                .ConfigureAwait(false));

        var postIds = posts.Select(post => post.Id).ToArray();
        var revisions = await database.Set<PostRevision>().AsNoTracking()
            .Where(revision => postIds.Contains(revision.PostId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var revisionIds = revisions.Select(revision => revision.Id).ToArray();
        var jobs = await database.Set<ProcessingJob>().AsNoTracking()
            .Where(job => revisionIds.Contains(job.RevisionId) &&
                (job.LlmVersion != null || job.ProcessorVersion == DvachFilter.ProcessorVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var attempts = jobIds.Length == 0
            ? []
            : await database.Set<JobAttempt>().AsNoTracking()
                .Where(attempt => jobIds.Contains(attempt.JobId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var analyses = await database.Set<Analysis>().AsNoTracking()
            .Where(analysis => revisionIds.Contains(analysis.RevisionId) && analysis.SemanticClass != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var analysisIds = analyses.Select(analysis => analysis.Id).ToArray();
        var profiles = analysisIds.Length == 0
            ? []
            : await database.Set<Profile>().AsNoTracking()
                .Where(profile => analysisIds.Contains(profile.AnalysisId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var currentRevisionIds = posts.Select(post => post.IngestedRevisionId).OfType<Guid>().ToArray();
        var observations = currentRevisionIds.Length == 0
            ? []
            : await (from observation in database.Set<ContactObservation>().AsNoTracking()
                     join contact in database.Set<Contact>().AsNoTracking() on observation.ContactId equals contact.Id
                     where currentRevisionIds.Contains(observation.RevisionId)
                     orderby contact.Platform, contact.NormalizedValue
                     select new ObservationRow(
                         observation.RevisionId, contact.Platform, contact.NormalizedValue, observation.RawValue,
                         observation.Confidence, observation.Provenance)).ToListAsync(cancellationToken).ConfigureAwait(false);

        var revisionViews = revisions.ToDictionary(revision => revision.Id, ToRevisionFacts);
        var profileByAnalysis = profiles.ToDictionary(profile => profile.AnalysisId);
        var attemptsByJob = attempts.ToLookup(attempt => attempt.JobId);
        var analysesByRevision = analyses.ToLookup(analysis => analysis.RevisionId);
        var jobsByRevision = jobs.ToLookup(job => job.RevisionId);
        var contactsByRevision = observations.ToLookup(row => row.RevisionId);
        var revisionsByPost = revisions.ToLookup(revision => revision.PostId);

        return new ReportSnapshot(
            sourceFacts,
            scanFacts,
            [.. posts.Select(post => ToPostFacts(post, revisionsByPost, revisionViews, jobsByRevision, attemptsByJob,
                analysesByRevision, profileByAnalysis, contactsByRevision))],
            await LoadOutsideOnlyAsync(outsidePosts, cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<ReportPostFacts>> LoadOutsideOnlyAsync(
        IReadOnlyList<OriginalPost> outsidePosts, CancellationToken cancellationToken)
    {
        var outsideIds = outsidePosts.Select(post => post.IngestedRevisionId).OfType<Guid>().ToArray();
        var revisions = outsideIds.Length == 0
            ? []
            : await database.Set<PostRevision>().AsNoTracking()
                .Where(revision => outsideIds.Contains(revision.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var views = revisions.ToDictionary(revision => revision.Id, ToRevisionFacts);
        return [.. outsidePosts.Select(post => new ReportPostFacts(
            post.Id, post.ExternalId, post.Url, post.PublishedAt, post.LastSeenAt, post.Excluded,
            post.IngestedRevisionId, post.IngestedRevisionId is { } currentId ? views.GetValueOrDefault(currentId) : null,
            [], [], [], []))];
    }

    private async Task<(IReadOnlyList<OriginalPost> Posts, IReadOnlyList<OriginalPost> Outside, int IdsInWindow, int OutsideCount)>
        LoadPostsAsync(Guid sourceId, SourceScan scan, CancellationToken cancellationToken)
    {
        if (scan.StartedAt is not { } started || scan.FinishedAt is not { } finished)
        {
            var all = await database.Set<OriginalPost>().AsNoTracking()
                .CountAsync(post => post.SourceId == sourceId, cancellationToken).ConfigureAwait(false);
            return ([], [], 0, all);
        }

        var window = await database.Set<OriginalPost>().AsNoTracking()
            .Where(post => post.SourceId == sourceId && post.LastSeenAt >= started && post.LastSeenAt <= finished)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var outside = await database.Set<OriginalPost>().AsNoTracking()
            .Where(post => post.SourceId == sourceId && (post.LastSeenAt < started || post.LastSeenAt > finished))
            .OrderByDescending(post => post.LastSeenAt)
            .Take(MaxOutsidePosts)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var outsideCount = await database.Set<OriginalPost>().AsNoTracking()
            .CountAsync(post => post.SourceId == sourceId && (post.LastSeenAt < started || post.LastSeenAt > finished),
                cancellationToken).ConfigureAwait(false);
        return ([.. window.OrderBy(post => Number(post.ExternalId))], [.. outside], window.Count, outsideCount);
    }

    private static ReportPostFacts ToPostFacts(
        OriginalPost post,
        ILookup<Guid, PostRevision> revisionsByPost,
        Dictionary<Guid, ReportRevisionFacts> revisionViews,
        ILookup<Guid, ProcessingJob> jobsByRevision,
        ILookup<Guid, JobAttempt> attemptsByJob,
        ILookup<Guid, Analysis> analysesByRevision,
        IReadOnlyDictionary<Guid, Profile> profileByAnalysis,
        ILookup<Guid, ObservationRow> contactsByRevision)
    {
        var revisions = revisionsByPost[post.Id]
            .OrderBy(revision => revision.ReceivedAt)
            .ThenBy(revision => revision.Id)
            .ToArray();
        var current = post.IngestedRevisionId is { } currentId ? revisionViews.GetValueOrDefault(currentId) : null;
        var jobs = new List<ReportJobFacts>();
        var analyses = new List<ReportAnalysisFacts>();
        foreach (var revision in revisions)
        {
            foreach (var job in jobsByRevision[revision.Id].OrderBy(item => item.AvailableAt).ThenBy(item => item.Id))
            {
                jobs.Add(new ReportJobFacts(job.Id, job.RevisionId, job.ProcessorVersion, job.LlmVersion, job.State, job.Attempts,
                    job.MaxAttempts, job.AvailableAt, job.FailureCategory,
                    [.. attemptsByJob[job.Id].OrderBy(attempt => attempt.Number).Select(ToAttemptFacts)]));
            }

            foreach (var analysis in analysesByRevision[revision.Id].OrderBy(item => item.CreatedAt).ThenBy(item => item.Id))
            {
                analyses.Add(ToAnalysisFacts(analysis, profileByAnalysis.GetValueOrDefault(analysis.Id)));
            }
        }

        return new ReportPostFacts(
            post.Id,
            post.ExternalId,
            post.Url,
            post.PublishedAt,
            post.LastSeenAt,
            post.Excluded,
            post.IngestedRevisionId,
            current,
            [.. revisions.Select(revision => revisionViews[revision.Id])],
            jobs,
            analyses,
            post.IngestedRevisionId is { } revisionId
                ? [.. contactsByRevision[revisionId].Select(row => new ReportContactFacts(
                    row.Platform, row.RawValue, row.NormalizedValue, row.Confidence, row.Provenance))]
                : []);
    }

    private static ReportAttemptFacts ToAttemptFacts(JobAttempt attempt) => new(
        attempt.Number, attempt.StartedAt, attempt.FinishedAt, attempt.ElapsedMilliseconds, attempt.MeasuredRequests,
        attempt.ReservedRequests, attempt.InputTokens, attempt.OutputTokens, attempt.CachedInputTokens,
        attempt.FailureCategory);

    private static ReportAnalysisFacts ToAnalysisFacts(Analysis analysis, Profile? profile)
    {
        var (fields, unsupported, preferences, description) = ReadObservations(analysis.Observations);
        var (evidence, omitted) = ReadAssessments(analysis.Assessments);
        return new ReportAnalysisFacts(
            analysis.Id,
            analysis.RevisionId,
            analysis.SemanticClass,
            analysis.SemanticReason,
            analysis.Model,
            analysis.Processor,
            analysis.PipelineVersion,
            analysis.SchemaVersion,
            analysis.Age,
            analysis.Location,
            analysis.GenderDescription,
            analysis.Interests,
            analysis.Goals,
            fields,
            unsupported,
            preferences,
            description,
            evidence,
            omitted,
            analysis.CreatedAt,
            profile is null ? null : new ReportProfileFacts(profile.Id, profile.State, profile.CreatedAt));
    }

    /// <summary>Подтверждённые поля и неподтверждённые имена из <c>Observations</c>.</summary>
    private static (IReadOnlyList<ReportFieldSpan> Fields, IReadOnlyList<string> Unsupported, string? Preferences, string? Description)
        ReadObservations(string json)
    {
        var root = ReadObject(json);
        if (root is not { } document)
            return ([], [], null, null);
        using (document)
        {
            List<ReportFieldSpan> fields = [];
            if (document.RootElement.TryGetProperty("fields", out var fieldsElement) &&
                fieldsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in fieldsElement.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Object ||
                        !field.Value.TryGetProperty("start", out var start) || !start.TryGetInt32(out var startValue) ||
                        !field.Value.TryGetProperty("length", out var length) || !length.TryGetInt32(out var lengthValue))
                        continue;
                    fields.Add(new ReportFieldSpan(field.Name, new TextSpan(startValue, lengthValue)));
                }
            }

            List<string> unsupported = [];
            if (document.RootElement.TryGetProperty("unsupportedFields", out var unsupportedElement) &&
                unsupportedElement.ValueKind == JsonValueKind.Array)
            {
                unsupported.AddRange(unsupportedElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty));
            }

            return (fields, unsupported, ReadText(document.RootElement, "preferences"), ReadText(document.RootElement, "description"));
        }
    }

    /// <summary>Координаты фрагмента-основания решения из <c>Assessments</c>.</summary>
    private static (TextSpan? Evidence, bool Omitted) ReadAssessments(string json)
    {
        var document = ReadObject(json);
        if (document is null)
            return (null, false);
        using (document)
        {
            var omitted = document.RootElement.TryGetProperty("evidenceOmitted", out var omittedElement) &&
                omittedElement.ValueKind == JsonValueKind.True;
            if (!document.RootElement.TryGetProperty("evidence", out var evidence) ||
                evidence.ValueKind != JsonValueKind.Object ||
                !evidence.TryGetProperty("start", out var start) || !start.TryGetInt32(out var startValue) ||
                !evidence.TryGetProperty("length", out var length) || !length.TryGetInt32(out var lengthValue))
                return (null, omitted);
            return (new TextSpan(startValue, lengthValue), omitted);
        }
    }

    private static JsonDocument? ReadObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            // Повреждённый JSON анализа показывается как отсутствие полей, а не как решение.
            return null;
        }
    }

    private static string? ReadText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static ReportScanFacts ToScanFacts(SourceScan scan)
    {
        var metrics = ReadMetrics(scan);
        return new ReportScanFacts(
            scan.Status,
            scan.Phase,
            scan.Failure,
            scan.StartedAt,
            scan.FinishedAt,
            scan.LastSuccessAt,
            scan.Applied,
            scan.Pending is not null,
            metrics?.Received ?? 0,
            metrics?.Closed ?? false,
            0,
            0,
            metrics);
    }

    private static DvachScanMetrics? ReadMetrics(SourceScan scan)
    {
        try
        {
            return JsonSerializer.Deserialize<DvachScanMetrics>(scan.Metrics);
        }
        catch (JsonException)
        {
            // Повреждённая строка метрик не должна скрывать снимок: счётчики считаются из постов.
            return null;
        }
    }

    private static ReportRevisionFacts ToRevisionFacts(PostRevision revision)
    {
        var (text, quotes, replies) = ReadMetadata(revision.RetrievalMetadata);
        return new ReportRevisionFacts(revision.Id, revision.Hash, revision.ReceivedAt, revision.IngestionReason,
            revision.IngestionReply, revision.IngestionContacts, revision.IngestionAmbiguous, text, quotes, replies);
    }

    /// <summary>Текст, цитаты и адреса ответов из <c>RetrievalMetadata</c> ревизии.</summary>
    private static (string? Text, IReadOnlyList<string> Quotes, IReadOnlyList<ReportReplyFacts> Replies) ReadMetadata(string json)
    {
        var document = ReadObject(json);
        if (document is null)
            return (null, [], []);
        using (document)
        {
            var root = document.RootElement;
            var text = ReadText(root, "text");
            List<string> quotes = [];
            if (root.TryGetProperty("quotes", out var quotesElement) && quotesElement.ValueKind == JsonValueKind.Array)
            {
                quotes.AddRange(quotesElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty));
            }

            List<ReportReplyFacts> replies = [];
            if (root.TryGetProperty("replies", out var repliesElement) && repliesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in repliesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("Board", out var board) || board.ValueKind != JsonValueKind.String ||
                        board.GetString() is not { } boardName ||
                        !item.TryGetProperty("Thread", out var thread) || !thread.TryGetInt64(out var threadNumber) ||
                        !item.TryGetProperty("Post", out var target) || !target.TryGetInt64(out var postNumber))
                        continue;
                    replies.Add(new ReportReplyFacts(boardName, threadNumber, postNumber));
                }
            }

            return (text, quotes, replies);
        }
    }

    private static long Number(string externalId) =>
        long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : long.MaxValue;

    private sealed record ObservationRow(
        Guid RevisionId,
        string Platform,
        string NormalizedValue,
        string RawValue,
        decimal Confidence,
        string Provenance);
}