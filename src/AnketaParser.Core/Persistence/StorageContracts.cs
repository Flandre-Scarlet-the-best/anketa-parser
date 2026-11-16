namespace AnketaParser.Core.Persistence;

public enum ProfileState { Candidate = 0, Reviewed = 1, Published = 2, Rejected = 3, Archived = 4 }
public enum LinkConfidence { Suggested = 0, VerifiedAccount = 1 }
public enum OwnershipCheckResult { Unknown, Confirmed, Refuted }
public sealed record PersonalBlacklistItem(Guid Id, Guid AnonId, string Reason, DateTimeOffset AddedAt);
public sealed record PostInput(string Source, string ExternalId, string Url, string Content, DateTimeOffset? PublishedAt, string RetrievalMetadata = "{}");
public sealed record AnalysisInput(Guid RevisionId, string Processor, string SchemaVersion, int? Age, string? Location, string? GenderDescription, string? Interests, string? Goals, string Observations, string Assessments);
public sealed record ContactInput(Guid RevisionId, string Platform, string RawValue, string NormalizedValue, decimal Confidence, string Provenance);
public sealed record PublicProfile(Guid Id, int? Age, string? Location, string? GenderDescription, string? Interests, string? Goals);
public sealed record JobLease(Guid JobId, Guid Token, Guid RevisionId, int Attempt, DateTimeOffset ExpiresAt);

/// <summary>Единый контракт хранения для будущих интерфейсов; технические Telegram ID не входят в публичную выдачу.</summary>
public interface IProductStore
{
    Task<Guid> ImportAsync(PostInput input, CancellationToken cancellationToken);
    Task<Guid> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken);
    Task<Guid> RegisterContactAsync(ContactInput input, CancellationToken cancellationToken);
    Task<Guid> ConfirmTelegramAsync(Guid contactId, long technicalId, string? username, string provenance, CancellationToken cancellationToken);
    Task<Guid> LinkAsync(Guid profileId, Guid contactId, Guid anonId, LinkConfidence confidence, string provenance, CancellationToken cancellationToken);
    Task UnlinkAsync(Guid linkId, string reason, CancellationToken cancellationToken);
    Task PublishAsync(Guid profileId, CancellationToken cancellationToken);
    Task ArchiveAsync(Guid profileId, string actor, string reason, CancellationToken cancellationToken);
    Task<PublicProfile?> LatestAsync(Guid anonId, CancellationToken cancellationToken);
    Task<Guid> RegisterBotUserAsync(long technicalId, CancellationToken cancellationToken);
    Task BlacklistAsync(Guid ownerId, Guid anonId, string reason, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonalBlacklistItem>> ReadBlacklistAsync(Guid ownerId, CancellationToken cancellationToken);
    Task SaveSettingsAsync(Guid ownerId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> ReadSettingsAsync(Guid ownerId, CancellationToken cancellationToken);
    Task<Guid> ComplainAsync(Guid ownerId, Guid profileId, Guid? contactId, string reason, CancellationToken cancellationToken);
    Task RecordOwnershipCheckAsync(Guid complaintId, OwnershipCheckResult result, string actor, string evidence, CancellationToken cancellationToken);
    Task<Guid> EnqueueAsync(Guid revisionId, string processorVersion, int maxAttempts, CancellationToken cancellationToken);
    Task<JobLease?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken);
    Task CompleteAsync(JobLease lease, string? failureCategory, CancellationToken cancellationToken);
}