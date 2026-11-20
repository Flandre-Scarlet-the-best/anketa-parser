using System.Globalization;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Проверки предусловий снимка. Множество постов одного скана доказуемо только при
/// одновременном выполнении условий: скан завершён одной фазой, журнал очищен,
/// применено ровно столько записей, сколько получено, и внутри окна скана находятся
/// ровно те же посты. Иначе отчёт не строится: смешивать два скана запрещено.
/// </summary>
public static class SnapshotBoundary
{
    /// <summary>Максимальное число номеров постов в фактических аргументах отказа.</summary>
    private const int MaxReportedPosts = 20;

    /// <summary>Проверяет предусловия снимка и выбрасывает <see cref="ReportDiagnosticException"/> с фактической причиной.</summary>
    /// <param name="scan">Состояние скана источника.</param>
    /// <param name="posts">Посты, попавшие в окно скана.</param>
    /// <exception cref="ReportDiagnosticException">Снимок нельзя доказать как один зафиксированный набор постов.</exception>
    public static void Validate(ReportScanFacts scan, IReadOnlyList<ReportPostFacts> posts)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(posts);

        if (scan.Status != "complete")
            throw new ReportDiagnosticException("scan_not_complete", Facts(("status", scan.Status), ("phase", scan.Phase), ("failure", scan.Failure)));
        if (scan.Phase != "complete")
            throw new ReportDiagnosticException("scan_phase_incomplete", Facts(("status", scan.Status), ("phase", scan.Phase)));
        if (scan.JournalPending)
            throw new ReportDiagnosticException("scan_journal_pending", Facts(("status", scan.Status), ("phase", scan.Phase)));
        if (scan.StartedAt is null || scan.FinishedAt is null)
            throw new ReportDiagnosticException("scan_bounds_missing",
                Facts(("startedAt", scan.StartedAt), ("finishedAt", scan.FinishedAt)));
        if (scan.FinishedAt < scan.StartedAt)
            throw new ReportDiagnosticException("scan_bounds_invalid",
                Facts(("startedAt", scan.StartedAt), ("finishedAt", scan.FinishedAt)));
        if (scan.Applied != scan.Received)
            throw new ReportDiagnosticException("scan_partially_applied",
                Facts(("received", scan.Received), ("applied", scan.Applied)));
        if (scan.IdsInWindow != scan.Received)
        {
            // Меньше полученного — скан возобновлялся и StartedAt перезаписан: часть постов
            // применена до сброса. Больше — в окно попали посты другого скана.
            var category = scan.IdsInWindow < scan.Received ? "snapshot_incomplete_composition" : "snapshot_contaminated";
            throw new ReportDiagnosticException(category, Facts(
                ("received", scan.Received),
                ("applied", scan.Applied),
                ("idsInWindow", scan.IdsInWindow),
                ("postsOutsideWindow", scan.PostsOutsideWindow),
                ("startedAt", scan.StartedAt),
                ("finishedAt", scan.FinishedAt)));
        }

        if (posts.Count != scan.IdsInWindow)
            throw new ReportDiagnosticException("snapshot_posts_mismatch",
                Facts(("readPosts", posts.Count), ("idsInWindow", scan.IdsInWindow)));

        var withoutRevision = posts.Where(post => post.CurrentRevision is null).Select(post => post.ExternalId).ToArray();
        if (withoutRevision.Length > 0)
            throw new ReportDiagnosticException("post_revision_missing",
                Facts(("posts", Describe(withoutRevision)), ("count", withoutRevision.Length)));

        if (scan.FinishedAt is { } finished)
        {
            var afterScan = posts
                .Where(post => post.CurrentRevision is { } revision && revision.ReceivedAt > finished)
                .Select(post => post.ExternalId)
                .ToArray();
            if (afterScan.Length > 0)
                throw new ReportDiagnosticException("revision_after_scan",
                    Facts(("posts", Describe(afterScan)), ("finishedAt", finished)));
        }
    }

    private static string Describe(string[] numbers) =>
        string.Join(", ", numbers.Take(MaxReportedPosts)) + (numbers.Length > MaxReportedPosts ? ", …" : string.Empty);

    private static IReadOnlyList<ReportDiagnosticFact> Facts(params (string Name, object? Value)[] facts) =>
        [.. facts.Select(fact => new ReportDiagnosticFact(fact.Name, Format(fact.Value)))];

    private static string Format(object? value) => value switch
    {
        null => "(нет)",
        DateTimeOffset moment => moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(нет)",
    };
}