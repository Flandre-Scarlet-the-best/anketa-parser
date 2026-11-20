using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>Готовый к отображению документ отчёта: сводка, детализация и ограничения данных.</summary>
public sealed record ReportDocument(
    DateTimeOffset GeneratedAt,
    string ToolVersion,
    ReportSnapshotView Snapshot,
    ReportRunMetadataView RunMetadata,
    IReadOnlyList<ReportNotice> Notices,
    IReadOnlyList<ReportCounterGroup> Summary,
    IReadOnlyList<ReportPostView> Posts,
    IReadOnlyList<ReportPostView> PostsOutsideWindow);

/// <summary>Сведения о снимке: источник, границы окна и счётчики скана.</summary>
public sealed record ReportSnapshotView(
    string SourceKey,
    string SourceUrl,
    string Status,
    string Phase,
    string? Failure,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? LastSuccessAt,
    int Received,
    int Applied,
    int IdsInWindow,
    int PostsOutsideWindow,
    bool Closed,
    DvachScanMetrics? Metrics);

/// <summary>Ограничение или предупреждение отчёта с причиной.</summary>
public sealed record ReportNotice(string Code, string Level, string Text)
{
    /// <summary>Уровень «ограничение данных»: сведения отсутствуют в схеме.</summary>
    public const string Limitation = "limitation";

    /// <summary>Уровень «предупреждение»: данные есть, но трактовка требует оговорки.</summary>
    public const string Warning = "warning";

    /// <summary>Уровень «пояснение»: как читать отчёт.</summary>
    public const string Info = "info";
}

/// <summary>Метаданные движка и параметров запуска из необязательного входного JSON.</summary>
public sealed record ReportRunMetadataView(
    string? Path,
    bool Loaded,
    IReadOnlyList<ReportMetadataItem> Facts,
    IReadOnlyList<ReportMetadataItem> All);

/// <summary>Один факт метаданных запуска с указанием источника.</summary>
public sealed record ReportMetadataItem(string Name, string Value);

/// <summary>Вид счётчика: абсолютное число, доля от знаменателя или сумма.</summary>
public enum ReportCounterKind { Count, Share, Sum }

/// <summary>Счётчик этапа с явным знаменателем; доля не определена при нулевом знаменателе.</summary>
public sealed record ReportCounter(
    string Id,
    string Label,
    long Value,
    long? Total,
    ReportCounterKind Kind,
    string? Note = null)
{
    /// <summary>Доля в процентах; null, если знаменатель отсутствует или равен нулю.</summary>
    public decimal? Percent => Kind == ReportCounterKind.Share && Total is > 0
        ? Math.Round(Value * 100m / Total.Value, 1)
        : null;
}

/// <summary>Группа счётчиков одного разреза отчёта.</summary>
public sealed record ReportCounterGroup(string Id, string Title, string? Caption, IReadOnlyList<ReportCounter> Counters);

/// <summary>Пост снимка со всеми тремя независимыми разрезами и детализацией ревизий.</summary>
public sealed record ReportPostView(
    string Number,
    string Url,
    string? OriginLink,
    string? PublishedAt,
    string LastSeenAt,
    string Stage,
    string StageLabel,
    bool ModerationExcluded,
    ReportFilterView Filter,
    ReportTextView Text,
    ReportLlmView Llm,
    IReadOnlyList<ReportRevisionView> Revisions,
    string SearchText);

/// <summary>Разрез первичного фильтра: решение, адресация и признаки контактов раздельно.</summary>
public sealed record ReportFilterView(
    string Reason,
    string ReasonLabel,
    string ReasonNote,
    string Addressing,
    string AddressingLabel,
    string AddressingNote,
    int ContactsCount,
    bool Ambiguous,
    bool AdmittedToLlm,
    string AdmittedNote);

/// <summary>Тексты поста и контакты: авторский текст, реконструкция маски, цитаты, адреса.</summary>
public sealed record ReportTextView(
    string? AuthorText,
    string TextNote,
    string? PreparedText,
    string PreparedStatus,
    string PreparedNote,
    IReadOnlyList<string> Quotes,
    IReadOnlyList<ReportReplyView> Replies,
    IReadOnlyList<ReportContactView> Contacts);

/// <summary>Адрес ответа в отображаемом виде.</summary>
public sealed record ReportReplyView(string Label, string? Url);

/// <summary>Наблюдение контакта автора.</summary>
public sealed record ReportContactView(
    string Platform,
    string RawValue,
    string NormalizedValue,
    string Confidence,
    string Provenance,
    string? TextNote);

/// <summary>Разрез решения модели: класс, основание, поля, фрагменты и задания.</summary>
public sealed record ReportLlmView(
    string Status,
    string StatusLabel,
    string? Note,
    string? Class,
    string? ClassLabel,
    string? Reason,
    string? ReasonLabel,
    string? Model,
    string? Engine,
    string? PipelineVersion,
    string? SchemaVersion,
    string? ProfileState,
    string? ProfileStateLabel,
    ReportEvidenceView Decision,
    IReadOnlyList<ReportFieldView> Fields,
    IReadOnlyList<string> UnsupportedFields,
    string? Preferences,
    string? Description,
    IReadOnlyList<ReportDecisionView> Decisions,
    string? CurrentJobState,
    IReadOnlyList<ReportJobView> Jobs);

/// <summary>Проверенный фрагмент-основание; подсветка допустима только при статусе <c>exact</c>.</summary>
public sealed record ReportEvidenceView(string Status, string? Fragment, string Note, TextSpan? Span)
{
    /// <summary>Координаты проверены и фрагмент воспроизведён точно.</summary>
    public const string Exact = "exact";

    /// <summary>Решение принято без сохранённого фрагмента.</summary>
    public const string Omitted = "omitted";

    /// <summary>Координаты отсутствуют.</summary>
    public const string Absent = "absent";

    /// <summary>Текст ревизии недоступен: смещения не проверяются.</summary>
    public const string NoText = "no_text";

    /// <summary>Координаты выходят за границы текста.</summary>
    public const string OutOfRange = "out_of_range";

    /// <summary>Фрагмент содержит символ маски.</summary>
    public const string MaskChar = "mask_char";

    /// <summary>Значение поля не найдено внутри фрагмента.</summary>
    public const string ValueNotInSpan = "value_not_in_span";
}

/// <summary>Подтверждённое поле анкеты с проверенным фрагментом.</summary>
public sealed record ReportFieldView(string Name, string Label, string? Value, ReportEvidenceView Evidence);

/// <summary>Одно модельное решение в истории ревизий поста.</summary>
public sealed record ReportDecisionView(
    string RevisionId,
    bool Current,
    string CreatedAt,
    string? Class,
    string? ClassLabel,
    string? Reason,
    string? ReasonLabel,
    string? Model,
    string? PipelineVersion,
    string? ProfileState);

/// <summary>Задание обработки с попытками.</summary>
public sealed record ReportJobView(
    Guid Id,
    string Kind,
    string KindLabel,
    string State,
    string StateLabel,
    string? LlmVersion,
    int AttemptCount,
    int MaxAttempts,
    string? FailureCategory,
    string AvailableAt,
    IReadOnlyList<ReportAttemptView> Attempts);

/// <summary>Попытка обработки: время, токены и категория отказа.</summary>
public sealed record ReportAttemptView(
    int Number,
    string StartedAt,
    string? FinishedAt,
    long? ElapsedMilliseconds,
    int MeasuredRequests,
    int ReservedRequests,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    string? FailureCategory);

/// <summary>Ревизия поста с признаком актуальности.</summary>
public sealed record ReportRevisionView(
    string Id,
    bool Current,
    string ReceivedAt,
    string Hash,
    string? Reason,
    string? Addressing,
    int Contacts,
    bool Ambiguous,
    int Analyses,
    int Jobs,
    bool HasText);