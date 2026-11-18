using AnketaParser.Core.Persistence;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class Source
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class OriginalPost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Excluded { get; set; }
    public Guid? IngestedRevisionId { get; set; }
}

public sealed class PostRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string RetrievalMetadata { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; }
    public string? IngestionReason { get; set; }
    public string? IngestionReply { get; set; }
    public int IngestionContacts { get; set; }
    public bool IngestionAmbiguous { get; set; }
}

internal sealed class SourceScan
{
    public Guid SourceId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string Status { get; set; } = "never_scanned";
    public string Phase { get; set; } = "idle";
    public string? Failure { get; set; }
    public int? HttpStatus { get; set; }
    public string? Pending { get; set; }
    public int Applied { get; set; }
    public string Metrics { get; set; } = "{}";
}

public sealed class Analysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public string Processor { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string? Location { get; set; }
    public string? GenderDescription { get; set; }
    public string? Interests { get; set; }
    public string? Goals { get; set; }
    public string Observations { get; set; } = "{}";
    public string Assessments { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnalysisId { get; set; }
    public Guid PostId { get; set; }
    public ProfileState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
}

public sealed class ContactObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContactId { get; set; }
    public Guid RevisionId { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public DateTimeOffset SeenAt { get; set; }
}

public sealed class TelegramAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TechnicalId { get; set; }
}

public sealed class TelegramUsername
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public string Provenance { get; set; } = string.Empty;
}

public sealed class ContactVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContactId { get; set; }
    public Guid AccountId { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; set; }
}

public sealed class Anon
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProfileLink
{
    public Guid? VerificationId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public Guid ContactId { get; set; }
    public Guid AnonId { get; set; }
    public LinkConfidence Confidence { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? UnlinkedAt { get; set; }
    public string? UnlinkReason { get; set; }
}

public sealed class BotUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TechnicalId { get; set; }
    public string Settings { get; set; } = "{}";
}

public sealed class BlacklistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid AnonId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class Complaint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid? ContactId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ModerationDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public Guid? ComplaintId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? OwnershipEvidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProcessingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public string ProcessorVersion { get; set; } = string.Empty;
    public int MaxAttempts { get; set; }
    public int Attempts { get; set; }
    public string State { get; set; } = "pending";
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public string? FailureCategory { get; set; }
}

public sealed class JobAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Number { get; set; }
    public Guid Token { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? FailureCategory { get; set; }
}