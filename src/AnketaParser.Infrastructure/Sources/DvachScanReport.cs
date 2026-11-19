namespace AnketaParser.Infrastructure.Sources;

public sealed record DvachScanReport(
    string Status,
    string Phase,
    string? Failure,
    int? HttpStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? RetryAt,
    int ConsecutiveFailures,
    bool HasPendingSnapshot,
    int Applied,
    DvachScanMetrics? Metrics);

public sealed record DvachScanMetrics(
    int Received = 0,
    bool Closed = false,
    int NewPosts = 0,
    int ChangedPosts = 0,
    int RepeatedPosts = 0,
    int Candidates = 0,
    int Ambiguous = 0,
    int Contacts = 0,
    int OpeningPosts = 0,
    int OtherReplies = 0,
    int NoOwnContact = 0,
    int Excluded = 0);

public sealed record DvachPostReport(
    string Status,
    string? Reason = null,
    string? Reply = null,
    int Revisions = 0,
    int Contacts = 0,
    bool Ambiguous = false,
    string? JobState = null,
    Guid? RevisionId = null);

public sealed record DvachQueueCount(string State, bool Current, int Count);