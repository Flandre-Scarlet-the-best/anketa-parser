using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Неизменяемый снимок фактов одного источника: одна строка скана, посты окна снимка
/// и отдельно посты, увиденные раньше и отсутствующие в снимке.
/// </summary>
public sealed record ReportSnapshot(
    ReportSourceFacts Source,
    ReportScanFacts Scan,
    IReadOnlyList<ReportPostFacts> Posts,
    IReadOnlyList<ReportPostFacts> PostsOutsideWindow);

/// <summary>Ключ и адрес источника в хранилище.</summary>
public sealed record ReportSourceFacts(Guid Id, string Key, string Url);

/// <summary>Состояние единственной сохраняемой строки скана (<c>SourceScan</c>).</summary>
public sealed record ReportScanFacts(
    string Status,
    string Phase,
    string? Failure,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? LastSuccessAt,
    int Applied,
    bool JournalPending,
    int Received,
    bool Closed,
    int IdsInWindow,
    int PostsOutsideWindow,
    DvachScanMetrics? Metrics);

/// <summary>Пост источника с актуальной ревизией, историей ревизий, заданиями и решениями.</summary>
public sealed record ReportPostFacts(
    Guid Id,
    string ExternalId,
    string Url,
    DateTimeOffset? PublishedAt,
    DateTimeOffset LastSeenAt,
    bool Excluded,
    Guid? CurrentRevisionId,
    ReportRevisionFacts? CurrentRevision,
    IReadOnlyList<ReportRevisionFacts> Revisions,
    IReadOnlyList<ReportJobFacts> Jobs,
    IReadOnlyList<ReportAnalysisFacts> Analyses,
    IReadOnlyList<ReportContactFacts> Contacts,
    bool DetailsLoaded = true);

/// <summary>Одна версия содержимого поста и решение первичного фильтра, сохранённое для неё.</summary>
public sealed record ReportRevisionFacts(
    Guid Id,
    string Hash,
    DateTimeOffset ReceivedAt,
    string? IngestionReason,
    string? IngestionReply,
    int IngestionContacts,
    bool IngestionAmbiguous,
    string? Text,
    IReadOnlyList<string> Quotes,
    IReadOnlyList<ReportReplyFacts> Replies);

/// <summary>Адрес ответа, восстановленный из метаданных ревизии.</summary>
public sealed record ReportReplyFacts(string Board, long Thread, long Post);

/// <summary>Задание обработки: сборщика (<c>LlmVersion</c> = null) или модели.</summary>
public sealed record ReportJobFacts(
    Guid Id,
    Guid RevisionId,
    string ProcessorVersion,
    string? LlmVersion,
    string State,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    string? FailureCategory,
    IReadOnlyList<ReportAttemptFacts> Attempts)
{
    /// <summary>
    /// Задание модели. Строка очереди создаётся вместе с допуском ревизии к анализу, поэтому
    /// ожидающее задание ещё не имеет <c>LlmVersion</c>: версия конвейера проставляется при
    /// захвате. Признаком модели служит сама постановка в очередь либо проставленная версия.
    /// </summary>
    public bool IsLlm =>
        LlmVersion is not null ||
        string.Equals(ProcessorVersion, DvachFilter.ProcessorVersion, StringComparison.Ordinal);

    /// <summary>Ожидающее задание модели, ещё не захваченное работником: версия конвейера не проставлена.</summary>
    public bool IsUnclaimedLlm => IsLlm && LlmVersion is null;
}

/// <summary>Попытка обработки задания с измеренными расходами.</summary>
public sealed record ReportAttemptFacts(
    int Number,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? ElapsedMilliseconds,
    int MeasuredRequests,
    int ReservedRequests,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    string? FailureCategory);

/// <summary>Решение анализа; модельное решение отличается заполненным <see cref="SemanticClass"/>.</summary>
public sealed record ReportAnalysisFacts(
    Guid Id,
    Guid RevisionId,
    string? SemanticClass,
    string? SemanticReason,
    string? Model,
    string Processor,
    string? PipelineVersion,
    string SchemaVersion,
    int? Age,
    string? Location,
    string? GenderDescription,
    string? Interests,
    string? Goals,
    IReadOnlyList<ReportFieldSpan> FieldSpans,
    IReadOnlyList<string> UnsupportedFields,
    string? Preferences,
    string? Description,
    TextSpan? EvidenceSpan,
    bool EvidenceOmitted,
    DateTimeOffset CreatedAt,
    ReportProfileFacts? Profile);

/// <summary>Сохранённые координаты подтверждённого поля (кодовая единица UTF-16).</summary>
public sealed record ReportFieldSpan(string Name, TextSpan Span, string? NormalizedValue = null);

/// <summary>Анкета, созданная по решению модели.</summary>
public sealed record ReportProfileFacts(Guid Id, ProfileState State, DateTimeOffset CreatedAt);

/// <summary>Наблюдение контакта текущей ревизии с координатой в тексте поста.</summary>
public sealed record ReportContactFacts(
    string Platform,
    string RawValue,
    string NormalizedValue,
    decimal Confidence,
    string Provenance);