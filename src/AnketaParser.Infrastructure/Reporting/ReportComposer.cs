using System.Globalization;

using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Сводит факты снимка в документ отчёта. Никаких обращений к сети, модели или хранилищу:
/// все решения принимаются по сохранённым данным, а непроверенное явно помечается ограничением.
/// </summary>
public static class ReportComposer
{
    /// <summary>Модель вынесла положительное решение.</summary>
    public const string StageProfile = SemanticContract.Profile;

    /// <summary>Модель вынесла отрицательное решение.</summary>
    public const string StageNotProfile = SemanticContract.NotProfile;

    /// <summary>Модель не смогла решить.</summary>
    public const string StageUncertain = SemanticContract.Uncertain;

    /// <summary>Задание модели ожидает обработки.</summary>
    public const string StagePending = "pending";

    /// <summary>Задание модели выполняется.</summary>
    public const string StageRunning = "running";

    /// <summary>Задание модели завершилось техническим отказом.</summary>
    public const string StageFailed = "failed";

    /// <summary>Задание модели отсутствует.</summary>
    public const string StageNoJob = "no_job";

    /// <summary>Задание завершено, но решения в хранилище нет.</summary>
    public const string StageSucceededWithoutDecision = "succeeded_without_decision";

    /// <summary>Пост не допущен к анализу модели.</summary>
    public const string StageNotAdmitted = "not_admitted";

    /// <summary>Решение есть только у устаревшей ревизии поста.</summary>
    public const string StageStaleOnly = "stale_only";

    /// <summary>Пост вне окна скана: задания и решения по нему не загружены.</summary>
    public const string StageDetailsNotLoaded = "details_not_loaded";

    /// <summary>Единственная дата, использованная в отчёте.</summary>
    private const string MomentFormat = "dd.MM.yyyy HH:mm:ss 'UTC'";

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        ["age"] = "возраст",
        ["gender"] = "пол (самоописание)",
        ["location"] = "локация",
        ["interests"] = "интересы",
        ["goals"] = "цель общения",
        ["preferences"] = "пожелания к собеседнику",
        ["description"] = "дополнительное описание",
    };

    private static readonly Dictionary<string, string> ReasonLabels = new(StringComparer.Ordinal)
    {
        ["invitation"] = "явное приглашение",
        ["implicit_invitation"] = "неявное приглашение",
        ["technical_contact"] = "контакт для посторонней задачи",
        ["unrelated"] = "иная цель",
        ["noise"] = "бессвязный текст",
        ["contact_only"] = "только контакт",
        ["ambiguous"] = "двусмысленность",
        ["missing_context"] = "недостаточно контекста",
    };

    /// <summary>Строит документ отчёта из фактов снимка.</summary>
    /// <param name="snapshot">Факты, прочитанные из хранилища.</param>
    /// <param name="runMetadata">Метаданные движка и параметров запуска либо null.</param>
    /// <param name="toolVersion">Версия инструмента.</param>
    /// <param name="generatedAt">Момент формирования отчёта.</param>
    /// <returns>Документ для рендеринга.</returns>
    /// <exception cref="ReportDiagnosticException">Снимок нельзя доказать как один зафиксированный набор постов.</exception>
    public static ReportDocument Build(
        ReportSnapshot snapshot,
        ReportRunMetadataView? runMetadata,
        string toolVersion,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(toolVersion);
        SnapshotBoundary.Validate(snapshot.Scan, snapshot.Posts);
        var posts = snapshot.Posts.Select(BuildPost).ToArray();
        var outside = snapshot.PostsOutsideWindow.Select(BuildPost).ToArray();
        return new ReportDocument(
            generatedAt,
            toolVersion,
            new ReportSnapshotView(
                snapshot.Source.Key,
                snapshot.Source.Url,
                snapshot.Scan.Status,
                snapshot.Scan.Phase,
                snapshot.Scan.Failure,
                snapshot.Scan.StartedAt,
                snapshot.Scan.FinishedAt,
                snapshot.Scan.LastSuccessAt,
                snapshot.Scan.Received,
                snapshot.Scan.Applied,
                snapshot.Scan.IdsInWindow,
                snapshot.Scan.PostsOutsideWindow,
                snapshot.Scan.Closed,
                snapshot.Scan.Metrics),
            runMetadata ?? new ReportRunMetadataView(null, false, [], []),
            BuildNotices(snapshot, posts, runMetadata),
            BuildSummary(snapshot, posts),
            posts,
            outside);
    }

    private static ReportPostView BuildPost(ReportPostFacts post)
    {
        var revision = post.CurrentRevision
            ?? throw new ReportDiagnosticException("post_revision_missing", [new ReportDiagnosticFact("postNumber", post.ExternalId)]);
        var filter = BuildFilter(post, revision);
        var text = BuildText(post, revision);
        var llm = post.DetailsLoaded ? BuildLlm(post, revision, filter, text) : DetailsNotLoaded(BuildLlm(post, revision, filter, text));
        return new ReportPostView(
            post.ExternalId,
            post.Url,
            SafeLink(post.Url),
            Format(post.PublishedAt),
            Format(post.LastSeenAt),
            llm.Status,
            llm.StatusLabel,
            post.Excluded,
            filter,
            text,
            llm,
            [.. post.Revisions.OrderByDescending(item => item.ReceivedAt).Select(item => BuildRevision(post, item))],
            SearchText(post, revision, text, llm));
    }

    private static ReportRevisionView BuildRevision(ReportPostFacts post, ReportRevisionFacts revision) => new(
        revision.Id.ToString("D", CultureInfo.InvariantCulture),
        post.CurrentRevisionId == revision.Id,
        Format(revision.ReceivedAt),
        revision.Hash,
        revision.IngestionReason,
        revision.IngestionReply,
        revision.IngestionContacts,
        revision.IngestionAmbiguous,
        post.Analyses.Count(analysis => analysis.RevisionId == revision.Id),
        post.Jobs.Count(job => job.RevisionId == revision.Id),
        revision.Text is not null);

    /// <summary>Решение фильтра, адресация и признаки контактов показываются раздельно.</summary>
    private static ReportFilterView BuildFilter(ReportPostFacts post, ReportRevisionFacts revision)
    {
        var reason = revision.IngestionReason ?? string.Empty;
        var (reasonLabel, reasonNote) = FilterDecisionLabel(reason);
        var addressing = revision.IngestionReply ?? string.Empty;
        var addressingLabel = AddressingLabel(addressing);
        var admitted = !post.Excluded && reason == DvachFilter.Candidate && post.CurrentRevisionId == revision.Id;
        var admittedNote = admitted
            ? "выполняется условие очереди: решение candidate, пост не исключён, ревизия актуальна"
            : post.Excluded
                ? "исключён модерацией: пост не ставится в очередь независимо от решения фильтра"
                : reason != DvachFilter.Candidate
                    ? "решение фильтра не допускает пост к анализу"
                    : "ревизия не актуальна";
        return new ReportFilterView(
            reason,
            reasonLabel,
            reasonNote,
            addressing,
            addressingLabel,
            "адресация — метаданные ответа; причиной отсева она не является",
            revision.IngestionContacts,
            revision.IngestionAmbiguous,
            admitted,
            admittedNote);
    }

    private static ReportTextView BuildText(ReportPostFacts post, ReportRevisionFacts revision)
    {
        var authorText = revision.Text;
        var contacts = post.Contacts
            .Select(contact => new ReportContactView(
                contact.Platform,
                contact.RawValue,
                contact.NormalizedValue,
                contact.Confidence.ToString("0.000", CultureInfo.InvariantCulture),
                contact.Provenance,
                ContactNote(contact, authorText)))
            .ToArray();
        var (prepared, status, note) = PrepareText(authorText, contacts);
        return new ReportTextView(
            authorText,
            authorText is null
                ? "текст ревизии недоступен: в RetrievalMetadata нет поля text"
                : "авторский текст из RetrievalMetadata ревизии; HTML поста в отчёт не выводится",
            prepared,
            status,
            note,
            revision.Quotes,
            [.. revision.Replies.Select(reply => new ReportReplyView(
                string.Create(CultureInfo.InvariantCulture, $"→ /{reply.Board}/res/{reply.Thread}#{reply.Post}"),
                ReplyLink(reply)))],
            contacts);
    }

    private static string ContactNote(ReportContactFacts contact, string? authorText) =>
        authorText is null ? "текст поста недоступен"
        : authorText.Contains(contact.RawValue, StringComparison.OrdinalIgnoreCase)
            ? "значение найдено в тексте поста"
            : "значение в текущем тексте не найдено: контакт мог быть удалён правкой";

    /// <summary>
    /// Реконструкция текста, подготовленного для модели. Отправленный текст не сохраняется,
    /// а хеш контрольной точки этим отчётом не сверяется; блок остаётся реконструкцией.
    /// </summary>
    private static (string? Prepared, string Status, string Note) PrepareText(
        string? authorText, IReadOnlyList<ReportContactView> contacts)
    {
        if (authorText is null)
            return (null, "unavailable", "точное воспроизведение невозможно: текст ревизии недоступен");
        var tokens = contacts.SelectMany(contact => new[] { contact.RawValue, contact.NormalizedValue })
            .Where(token => token.Length > 0)
            .ToArray();
        var prepared = LlmInput.Redact(authorText, tokens);
        if (prepared.Length != authorText.Length)
            return (null, "unavailable", "точное воспроизведение невозможно: длина маски отличается от текста");
        for (var index = 0; index < authorText.Length; index++)
        {
            if (prepared[index] != authorText[index] && prepared[index] != SemanticContract.Redacted)
                return (null, "unavailable", "точное воспроизведение невозможно: маска изменила неконтактный символ");
        }

        foreach (var token in tokens)
        {
            for (var offset = 0; offset < authorText.Length;)
            {
                var found = authorText.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;
                if (prepared.AsSpan(found, token.Length).ContainsAnyExcept(SemanticContract.Redacted))
                    return (null, "unavailable", "точное воспроизведение невозможно: известный контакт остался в тексте");
                offset = found + 1;
            }
        }

        return (prepared, "reconstructed",
            "реконструкция маски по текущим наблюдениям контактов: отправленный текст не сохраняется, хеш контрольной точки в отчёте не сверяется");
    }

    /// <summary>Проверяет сохранённые координаты и возвращает фрагмент только при точном воспроизведении.</summary>
    private static ReportEvidenceView Verify(TextSpan? span, string? text, bool omitted, string? value)
    {
        if (span is null)
        {
            return omitted
                ? new ReportEvidenceView(ReportEvidenceView.Omitted, null,
                    "решение принято без сохранённого фрагмента (evidenceOmitted = true)", null)
                : new ReportEvidenceView(ReportEvidenceView.Absent, null, "координаты фрагмента не сохранены", null);
        }

        if (text is null)
        {
            return new ReportEvidenceView(ReportEvidenceView.NoText, null,
                "текст ревизии недоступен: смещения не проверяются, подсветка не выполняется", null);
        }

        if (span.Start < 0 || span.Length <= 0 || span.Start > text.Length || span.Length > text.Length - span.Start)
        {
            return new ReportEvidenceView(ReportEvidenceView.OutOfRange, null, string.Create(CultureInfo.InvariantCulture,
                $"координаты {span.Start}+{span.Length} выходят за границы текста длиной {text.Length} (UTF-16)"), null);
        }

        var fragment = text.Substring(span.Start, span.Length);
        if (fragment.Contains(SemanticContract.Redacted, StringComparison.Ordinal))
        {
            return new ReportEvidenceView(ReportEvidenceView.MaskChar, fragment,
                "фрагмент содержит символ маски █: исходный контакт не выделяется как цитата модели", null);
        }

        if (value is not null && !fragment.Contains(value, StringComparison.Ordinal))
        {
            return new ReportEvidenceView(ReportEvidenceView.ValueNotInSpan, fragment,
                "значение поля не найдено внутри фрагмента: подтверждение не выполнено, подсветка не выполняется", null);
        }

        return new ReportEvidenceView(ReportEvidenceView.Exact, fragment,
            "фрагмент восстановлен по сохранённым координатам точно", span);
    }

    private static ReportLlmView BuildLlm(
        ReportPostFacts post, ReportRevisionFacts revision, ReportFilterView filter, ReportTextView text)
    {
        var current = post.Analyses.Where(analysis => analysis.RevisionId == revision.Id)
            .OrderBy(analysis => analysis.CreatedAt).ThenBy(analysis => analysis.Id).ToArray();
        var stale = post.Analyses.Where(analysis => analysis.RevisionId != revision.Id).ToArray();
        var effective = current.Length == 0 ? null : current[^1];
        var currentJobs = post.Jobs.Where(job => job.RevisionId == revision.Id).ToArray();
        var llmJobs = currentJobs.Where(job => job.IsLlm).OrderBy(job => job.AvailableAt).ThenBy(job => job.Id).ToArray();
        var collectorJobs = currentJobs.Where(job => !job.IsLlm).ToArray();

        var (status, note) = ResolveStage(filter, effective, stale, llmJobs);
        if (status == StageNoJob && collectorJobs.Length > 0)
            note = "задание модели отсутствует; задание сборщика: " + string.Join(", ", collectorJobs.Select(JobStateLabel));

        var decision = effective is null
            ? new ReportEvidenceView(ReportEvidenceView.Absent, null, "решения модели по актуальной ревизии нет", null)
            : Verify(effective.EvidenceSpan, text.PreparedText, effective.EvidenceOmitted, null);
        var fields = effective is null
            ? []
            : BuildFields(effective, text.PreparedText);
        var decisions = post.Analyses
            .OrderBy(analysis => analysis.CreatedAt).ThenBy(analysis => analysis.Id)
            .Select(analysis => new ReportDecisionView(
                analysis.RevisionId.ToString("D", CultureInfo.InvariantCulture),
                analysis.RevisionId == revision.Id,
                Format(analysis.CreatedAt),
                analysis.SemanticClass,
                ClassLabel(analysis.SemanticClass),
                analysis.SemanticReason,
                ReasonLabel(analysis.SemanticReason),
                analysis.Model,
                analysis.PipelineVersion,
                analysis.Profile is { } profile ? ProfileStateLabel(profile.State) : null))
            .ToArray();
        return new ReportLlmView(
            status,
            StageLabel(status),
            note,
            effective?.SemanticClass,
            ClassLabel(effective?.SemanticClass),
            effective?.SemanticReason,
            ReasonLabel(effective?.SemanticReason),
            effective?.Model,
            effective?.Processor,
            effective?.PipelineVersion,
            effective?.SchemaVersion,
            effective?.Profile?.State.ToString(),
            effective?.Profile is { } profile2 ? ProfileStateLabel(profile2.State) : null,
            decision,
            fields,
            effective?.UnsupportedFields.Select(UnsupportedLabel).ToArray() ?? [],
            effective?.Preferences,
            effective?.Description,
            decisions,
            llmJobs.LastOrDefault()?.State,
            [.. post.Jobs.OrderBy(job => job.AvailableAt).ThenBy(job => job.Id).Select(BuildJob)]);
    }

    /// <summary>
    /// Сведения о заданиях и решениях поста вне окна скана не читались из хранилища:
    /// отчёт сообщает об этом вместо утверждения, что задания и решения отсутствуют.
    /// </summary>
    private static ReportLlmView DetailsNotLoaded(ReportLlmView llm) =>
        llm with
        {
            Status = StageDetailsNotLoaded,
            StatusLabel = StageLabel(StageDetailsNotLoaded),
            Note = "пост вне окна последнего скана: задания и решения по нему в отчёт не загружались",
            Class = null,
            ClassLabel = null,
            Reason = null,
            ReasonLabel = null,
            Model = null,
            Engine = null,
            PipelineVersion = null,
            SchemaVersion = null,
            ProfileState = null,
            ProfileStateLabel = null,
            Decision = new ReportEvidenceView(ReportEvidenceView.Absent, null,
                "сведения о решении модели для поста вне окна скана не загружены", null),
            Fields = [],
            UnsupportedFields = [],
            Preferences = null,
            Description = null,
            Decisions = [],
            CurrentJobState = null,
            Jobs = [],
        };

    private static (string Status, string? Note) ResolveStage(
        ReportFilterView filter,
        ReportAnalysisFacts? effective,
        ReportAnalysisFacts[] stale,
        ReportJobFacts[] llmJobs)
    {
        if (!filter.AdmittedToLlm)
        {
            return (StageNotAdmitted, effective is null
                ? "пост не проходил анализ модели: решение фильтра или исключение модерацией"
                : "решение модели сохранено, но пост не допущен к анализу сейчас: в сводку оно не входит");
        }

        if (effective is not null)
            return (effective.SemanticClass ?? StageNoJob, null);
        if (stale.Length > 0 && llmJobs.Length == 0)
        {
            return (StageStaleOnly,
                "решение модели есть только у устаревшей ревизии поста: для актуальной ревизии результата нет, к классам решения оно не относится");
        }

        if (llmJobs.Length == 0)
            return (StageNoJob, "задание модели отсутствует: пост допущен, но в очередь модели не ставился");

        var last = llmJobs[^1];
        return last.State switch
        {
            "pending" => (StagePending, last.IsUnclaimedLlm
                ? "задание модели ожидает обработки: версия конвейера проставляется при захвате задания"
                : "задание модели ожидает обработки"),
            "running" => (StageRunning, "задание модели выполняется"),
            "failed" => (StageFailed, "технический отказ задания: " + (last.FailureCategory ?? "категория не записана")),
            _ => (StageSucceededWithoutDecision, "задание завершено, но решения в хранилище нет"),
        };
    }

    private static IReadOnlyList<ReportFieldView> BuildFields(ReportAnalysisFacts analysis, string? text) =>
        [.. analysis.FieldSpans.Select(span =>
        {
            var raw = FieldValue(analysis, span.Name);
            var evidence = Verify(span.Span, text, omitted: false, raw);
            var display = evidence.Span is not null ? span.NormalizedValue ?? raw : raw;
            return new ReportFieldView(span.Name, FieldLabels.GetValueOrDefault(span.Name, span.Name), display, evidence);
        })];

    private static string? FieldValue(ReportAnalysisFacts analysis, string name) => name switch
    {
        "age" => analysis.Age?.ToString(CultureInfo.InvariantCulture),
        "gender" => analysis.GenderDescription,
        "location" => analysis.Location,
        "interests" => analysis.Interests,
        "goals" => analysis.Goals,
        "preferences" => analysis.Preferences,
        "description" => analysis.Description,
        _ => null,
    };

    private static ReportJobView BuildJob(ReportJobFacts job) => new(
        job.Id,
        job.IsLlm ? "llm" : "collector",
        JobKindLabel(job),
        job.State,
        JobStateLabel(job),
        job.LlmVersion,
        job.AttemptCount,
        job.MaxAttempts,
        job.FailureCategory,
        Format(job.AvailableAt),
        [.. job.Attempts.Select(attempt => new ReportAttemptView(
            attempt.Number,
            Format(attempt.StartedAt),
            attempt.FinishedAt is { } finished ? Format(finished) : null,
            attempt.ElapsedMilliseconds,
            attempt.MeasuredRequests,
            attempt.ReservedRequests,
            attempt.InputTokens,
            attempt.OutputTokens,
            attempt.CachedInputTokens,
            attempt.FailureCategory))]);

    private static string JobStateLabel(ReportJobFacts job) => job.State switch
    {
        "pending" => "ожидает",
        "running" => "выполняется",
        "succeeded" => "завершено",
        "failed" => "отказ",
        _ => job.State,
    };

    /// <summary>Вид задания с оговоркой для ожидающей строки очереди без проставленной версии.</summary>
    private static string JobKindLabel(ReportJobFacts job)
    {
        if (!job.IsLlm)
            return "задание сборщика";
        return job.IsUnclaimedLlm ? "задание модели: ожидает захвата" : "задание модели";
    }

    private static string StageLabel(string stage) => stage switch
    {
        StageProfile => "решение модели: похоже на анкету",
        StageNotProfile => "решение модели: не анкета",
        StageUncertain => "решение модели: неопределённо",
        StagePending => "ожидает обработки моделью",
        StageRunning => "обрабатывается моделью",
        StageFailed => "технический отказ модели",
        StageNoJob => "результата модели нет: задание отсутствует",
        StageSucceededWithoutDecision => "задание завершено без решения",
        StageNotAdmitted => "не допущен к анализу модели",
        StageStaleOnly => "результат только у устаревшей ревизии",
        StageDetailsNotLoaded => "пост вне окна скана: задания и решения не загружены",
        _ => stage,
    };

    private static string? ClassLabel(string? value) => value switch
    {
        null or "" => null,
        StageProfile => "анкета",
        StageNotProfile => "не анкета",
        StageUncertain => "неопределённо",
        _ => value,
    };

    private static string? ReasonLabel(string? value) =>
        value is null ? null : ReasonLabels.GetValueOrDefault(value, value);

    private static string UnsupportedLabel(string name)
    {
        var label = FieldLabels.GetValueOrDefault(name, name);
        return string.Create(CultureInfo.InvariantCulture, $"{label}: значение не подтверждено координатами и в анкету не попало");
    }

    private static string ProfileStateLabel(ProfileState state) => state switch
    {
        ProfileState.Candidate => "кандидат",
        ProfileState.Reviewed => "рассмотрена",
        ProfileState.Published => "опубликована",
        ProfileState.Rejected => "отклонена",
        ProfileState.Archived => "в архиве",
        _ => state.ToString(),
    };

    private static string? SafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Length > 0 && !url.Contains('<', StringComparison.Ordinal) && !url.Contains('>', StringComparison.Ordinal)
            ? url
            : null;

    private static string? ReplyLink(ReportReplyFacts reply) =>
        reply.Board.Length is > 0 and <= 16 && reply.Board.All(char.IsAsciiLetterOrDigit) && reply.Thread > 0 && reply.Post > 0
            ? string.Create(CultureInfo.InvariantCulture, $"https://2ch.org/{reply.Board}/res/{reply.Thread}.html#{reply.Post}")
            : null;

    private static string SearchText(
        ReportPostFacts post, ReportRevisionFacts revision, ReportTextView text, ReportLlmView llm)
    {
        var parts = new List<string>
        {
            post.ExternalId,
            revision.IngestionReason ?? string.Empty,
            revision.IngestionReply ?? string.Empty,
            llm.Class ?? string.Empty,
            llm.Reason ?? string.Empty,
            llm.Status,
            text.AuthorText ?? string.Empty,
            text.PreparedText ?? string.Empty,
            llm.Decision.Fragment ?? string.Empty,
        };
        parts.AddRange(text.Quotes);
        parts.AddRange(text.Contacts.Select(contact => contact.RawValue));
        parts.AddRange(text.Contacts.Select(contact => contact.NormalizedValue));
        parts.AddRange(llm.Fields.Select(field => field.Value ?? string.Empty));
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>Ограничения данных и предупреждения, обязательные для честной трактовки отчёта.</summary>
    private static List<ReportNotice> BuildNotices(
        ReportSnapshot snapshot, ReportPostView[] posts, ReportRunMetadataView? runMetadata)
    {
        List<ReportNotice> notices =
        [
            new("mask", ReportNotice.Limitation,
                "Текст, отправленный модели, и хеш маски в базе не сохраняются: блок «подготовленный текст» — реконструкция по текущим наблюдениям контактов."),
            new("evidence", ReportNotice.Info,
                "Фрагменты-основания хранятся координатами UTF-16 и восстанавливаются из текста ревизии; подсветка выполняется только после проверки смещений."),
            new("model_metadata", ReportNotice.Limitation,
                "В схеме нет хеша файла модели, параметров сэмплинга, endpoint и имени профиля: такие сведения берутся только из переданных метаданных запуска."),
            new("scan_history", ReportNotice.Limitation,
                "Истории сканов нет: SourceScan описывает только последний скан источника, поэтому снимок строится по его границам."),
            new("engine_sources", ReportNotice.Info,
                "Движок и модель в детализации — сохранённые решения модели; сведения шапки из метаданных запуска помечены отдельным источником."),
        ];
        if (snapshot.Scan.PostsOutsideWindow > 0)
        {
            notices.Add(new("outside_posts", ReportNotice.Warning, string.Create(CultureInfo.InvariantCulture,
                $"Обнаружены посты вне окна последнего скана: {snapshot.Scan.PostsOutsideWindow}. Они увидены раньше и в снимок не входят: решения и анкеты по ним сохранены, но к этому снимку не относятся.")));
        }

        if (runMetadata is not { Loaded: true })
        {
            notices.Add(new("run_metadata", ReportNotice.Info,
                "Метаданные запуска не переданы: сведения о движке и модели ограничены сохранёнными решениями."));
        }
        else if (runMetadata.Truncated)
        {
            notices.Add(new("run_metadata_truncated", ReportNotice.Limitation,
                "Список плоских значений метаданных запуска показан не полностью; сводка распознанных полей построена по всему файлу."));
        }

        var unverified = posts.Count(post => post.Llm.Decision.Status is not
            (ReportEvidenceView.Exact or ReportEvidenceView.Omitted or ReportEvidenceView.Absent));
        if (unverified > 0)
        {
            notices.Add(new("unverified_evidence", ReportNotice.Warning, string.Create(CultureInfo.InvariantCulture,
                $"У {unverified} решений фрагмент-основание не подтверждён точными координатами: фрагмент показан без подсветки.")));
        }

        var maskUnavailable = posts.Count(post => post.Filter.AdmittedToLlm && post.Text.PreparedStatus != "reconstructed");
        if (maskUnavailable > 0)
        {
            notices.Add(new("mask_unavailable", ReportNotice.Warning, string.Create(CultureInfo.InvariantCulture,
                $"Для {maskUnavailable} постов подготовленный текст не реконструирован: показан авторский текст с ограничением.")));
        }

        return notices;
    }

    /// <summary>Счётчики этапов и причин с явными знаменателями.</summary>
    private static List<ReportCounterGroup> BuildSummary(ReportSnapshot snapshot, ReportPostView[] posts)
    {
        _ = snapshot;
        var total = (long)posts.Length;
        long Of(Func<ReportPostView, bool> predicate) => posts.Count(predicate);
        var candidate = Of(post => post.Filter.Reason == DvachFilter.Candidate);
        var opening = Of(post => post.Filter.Reason == DvachFilter.OpeningPost);
        var noContact = Of(post => post.Filter.Reason == DvachFilter.NoOwnContact);
        var unresolved = Of(post => post.Filter.Reason == DvachFilter.UnresolvedContact);
        var unknownReason = total - candidate - opening - noContact - unresolved;
        var excluded = Of(post => post.ModerationExcluded);
        var admitted = Of(post => post.Filter.AdmittedToLlm);
        var staleRevisions = (long)posts.Sum(post => Math.Max(0, post.Revisions.Count - 1));
        var stalePosts = Of(post => post.Revisions.Count > 1);
        var profileDecisions = Of(post => post.Filter.AdmittedToLlm && post.Stage == StageProfile);
        var notProfileDecisions = Of(post => post.Filter.AdmittedToLlm && post.Stage == StageNotProfile);
        var uncertainDecisions = Of(post => post.Filter.AdmittedToLlm && post.Stage == StageUncertain);
        var results = profileDecisions + notProfileDecisions + uncertainDecisions;
        var candidateProfiles = Of(post => post.Filter.AdmittedToLlm && post.Llm.ProfileState == nameof(ProfileState.Candidate));
        var llmJobs = posts.SelectMany(post => post.Llm.Jobs).Where(job => job.Kind == "llm").ToArray();
        var attempts = llmJobs.SelectMany(job => job.Attempts).ToArray();
        var measured = attempts.Where(attempt => attempt.ElapsedMilliseconds is not null)
            .Select(attempt => attempt.ElapsedMilliseconds!.Value).ToArray();
        var inputTokens = attempts.Where(attempt => attempt.InputTokens is not null).Sum(attempt => attempt.InputTokens!.Value);
        var outputTokens = attempts.Where(attempt => attempt.OutputTokens is not null).Sum(attempt => attempt.OutputTokens!.Value);
        var cachedTokens = attempts.Where(attempt => attempt.CachedInputTokens is not null).Sum(attempt => attempt.CachedInputTokens!.Value);

        List<ReportCounterGroup> groups =
        [
            new("stage1", "Этап 1. Снимок треда", "знаменатель — посты снимка",
            [
                new("snapshot.posts", "постов в снимке", total, null, ReportCounterKind.Count,
                    "множество ExternalId постов, попавших в окно скана"),
                new("revision.stale", "устаревших ревизий", staleRevisions, null, ReportCounterKind.Count,
                    "ревизии, не совпадающие с OriginalPost.IngestedRevisionId"),
                new("revision.stale_posts", "постов с устаревшими ревизиями", stalePosts, total, ReportCounterKind.Share, null),
                new("text.quotes", "цитат в снимке", posts.Sum(post => post.Text.Quotes.Count), null, ReportCounterKind.Count, null),
                new("contacts.observed", "наблюдений контактов", posts.Sum(post => post.Text.Contacts.Count), null,
                    ReportCounterKind.Count, "у актуальных ревизий постов снимка"),
            ]),
            new("stage2", "Этап 2. Первичный фильтр", "знаменатель — посты снимка",
            [
                new("filter.candidate", "допущен", candidate, total, ReportCounterKind.Share, "решение фильтра candidate"),
                new("filter.opening_post", "открывающий пост", opening, total, ReportCounterKind.Share, null),
                new("filter.no_own_contact", "нет своего контакта", noContact, total, ReportCounterKind.Share, null),
                new("filter.unresolved_contact", "возможный контакт не нормализован", unresolved, total, ReportCounterKind.Share, null),
                new("filter.unknown", "решение не зафиксировано", unknownReason, total, ReportCounterKind.Share,
                    "нет значения в данных либо оно вне контракта фильтра"),
                new("moderation.excluded", "исключено модерацией", excluded, total, ReportCounterKind.Share,
                    "независимый признак OriginalPost.Excluded; решение фильтра сохраняется"),
                new("filter.admitted", "допущено к анализу модели", admitted, total, ReportCounterKind.Share,
                    "candidate, не исключён, ревизия актуальна"),
                new("filter.ambiguous", "разные значения контактов одной платформы", Of(post => post.Filter.Ambiguous), total,
                    ReportCounterKind.Share, "признак IngestionAmbiguous"),
                new("filter.contacts", "распознанных контактов", posts.Sum(post => (long)post.Filter.ContactsCount), null,
                    ReportCounterKind.Count, "сумма IngestionContacts по актуальным ревизиям"),
                new("addressing.op", "сам OP", Of(post => post.Filter.Addressing == "op"), total, ReportCounterKind.Share, null),
                new("addressing.op_reply", "ответ на OP", Of(post => post.Filter.Addressing == "op_reply"), total,
                    ReportCounterKind.Share, null),
                new("addressing.none", "без адресации", Of(post => post.Filter.Addressing == "none"), total,
                    ReportCounterKind.Share, null),
                new("addressing.other_reply", "ответ другому посту", Of(post => post.Filter.Addressing == DvachFilter.OtherReply),
                    total, ReportCounterKind.Share, "адресация, а не причина отсева"),
            ]),
            new("stage3", "Этап 3. Решение модели", "знаменатель — посты, допущенные к анализу",
            [
                new("llm.profile", "решение profile", profileDecisions, admitted, ReportCounterKind.Share, null),
                new("llm.not_profile", "решение not_profile", notProfileDecisions, admitted, ReportCounterKind.Share, null),
                new("llm.uncertain", "решение uncertain", uncertainDecisions, admitted, ReportCounterKind.Share, null),
                new("llm.no_result", "без результата модели", admitted - results, admitted, ReportCounterKind.Share,
                    "ожидание, выполнение или технический отказ; отсутствие результата не равно not_profile"),
                new("llm.pending", "ожидает обработки", Of(post => post.Filter.AdmittedToLlm && post.Stage == StagePending),
                    admitted, ReportCounterKind.Share, null),
                new("llm.running", "выполняется", Of(post => post.Filter.AdmittedToLlm && post.Stage == StageRunning),
                    admitted, ReportCounterKind.Share, null),
                new("llm.failed", "технический отказ", Of(post => post.Filter.AdmittedToLlm && post.Stage == StageFailed),
                    admitted, ReportCounterKind.Share, "категория отказа указана в детализации поста"),
                new("llm.no_job", "задание модели отсутствует",
                    Of(post => post.Filter.AdmittedToLlm && post.Stage == StageNoJob), admitted, ReportCounterKind.Share, null),
                new("llm.stale_only", "результат только у устаревшей ревизии",
                    Of(post => post.Filter.AdmittedToLlm && post.Stage == StageStaleOnly), admitted, ReportCounterKind.Share, null),
                new("llm.succeeded_without_decision", "задание завершено без решения",
                    Of(post => post.Filter.AdmittedToLlm && post.Stage == StageSucceededWithoutDecision), admitted,
                    ReportCounterKind.Share, null),
                new("profile.candidate", "анкет в состоянии Candidate", candidateProfiles, results, ReportCounterKind.Share,
                    "Profile(State = Candidate), созданные положительным решением модели"),
            ]),
            new("conversion", "Конверсия между этапами", "доли считаются от собственного знаменателя",
            [
                new("conversion.snapshot_admitted", "снимок → допущено к анализу", admitted, total, ReportCounterKind.Share, null),
                new("conversion.admitted_result", "допущено → есть решение модели", results, admitted,
                    ReportCounterKind.Share, null),
                new("conversion.result_profile", "решение → анкета", profileDecisions, results, ReportCounterKind.Share, null),
            ]),
            new("causes.filter", "Причины отсева первичного фильтра", "доля от постов снимка",
            [
                new("cause.opening_post", "открывающий пост", opening, total, ReportCounterKind.Share, null),
                new("cause.no_own_contact", "нет своего контакта", noContact, total, ReportCounterKind.Share, null),
                new("cause.unresolved_contact", "возможный контакт не нормализован", unresolved, total, ReportCounterKind.Share, null),
                new("cause.moderation_excluded", "исключён модерацией", excluded, total, ReportCounterKind.Share,
                    "причина уровня поста: решение фильтра при этом не меняется"),
                new("cause.unknown", "решение не зафиксировано", unknownReason, total, ReportCounterKind.Share, null),
            ]),
            new("jobs", "Задания и расходы модели", "знаменатели указаны в каждой строке",
            [
                new("jobs.total", "заданий модели", llmJobs.Length, null, ReportCounterKind.Count, null),
                new("attempts.total", "попыток обработки", attempts.Length, null, ReportCounterKind.Count, null),
                new("attempts.closed", "закрытых попыток", attempts.Count(attempt => attempt.FinishedAt is not null),
                    attempts.Length, ReportCounterKind.Share, "FinishedAt заполнен"),
                new("attempts.failed", "попыток с отказом", attempts.Count(attempt => attempt.FailureCategory is not null),
                    attempts.Length, ReportCounterKind.Share, null),
                new("attempts.elapsed_mean", "среднее время попытки, мс",
                    measured.Length == 0 ? 0 : (long)Math.Round(measured.Average(), MidpointRounding.AwayFromZero), null,
                    ReportCounterKind.Count, "по попыткам с измеренным временем"),
                new("tokens.input", "входных токенов", inputTokens, null, ReportCounterKind.Count,
                    "сумма по попыткам, где провайдер дал статистику"),
                new("tokens.output", "выходных токенов", outputTokens, null, ReportCounterKind.Count, null),
                new("tokens.cached", "кэшированных входных токенов", cachedTokens, null, ReportCounterKind.Count, null),
                new("requests.measured", "измеренных запросов",
                    attempts.Sum(attempt => (long)attempt.MeasuredRequests), null, ReportCounterKind.Count, null),
                new("requests.reserved", "зарезервированных запросов",
                    attempts.Sum(attempt => (long)attempt.ReservedRequests), null, ReportCounterKind.Count, null),
            ]),
        ];

        var decisions = posts
            .Where(post => post.Filter.AdmittedToLlm && post.Llm.Class is not null)
            .GroupBy(post => (Class: post.Llm.Class!, Reason: post.Llm.Reason ?? "unknown"))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Class, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
            .Select(group => new ReportCounter(
                string.Create(CultureInfo.InvariantCulture, $"decision.{group.Key.Class}.{group.Key.Reason}"),
                ClassLabel(group.Key.Class) + ": " + (ReasonLabel(group.Key.Reason) ?? group.Key.Reason),
                group.Count(), results, ReportCounterKind.Share, null))
            .ToArray();
        if (decisions.Length > 0)
        {
            groups.Add(new("causes.decision", "Причины решений модели", "доля от решений модели", decisions));
        }

        var combinations = posts
            .GroupBy(post => (Reason: post.Filter.Reason, Addressing: post.Filter.Addressing))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Addressing, StringComparer.Ordinal)
            .Select(group => new ReportCounter(
                string.Create(CultureInfo.InvariantCulture, $"cross.{group.Key.Reason}.{group.Key.Addressing}"),
                FilterDecisionLabel(group.Key.Reason).Label + " · " + AddressingLabel(group.Key.Addressing),
                group.Count(), total, ReportCounterKind.Share, null))
            .ToArray();
        if (combinations.Length > 0)
        {
            groups.Add(new("causes.cross", "Сочетания решения фильтра и адресации", "доля от постов снимка", combinations));
        }

        var failures = attempts.Where(attempt => attempt.FailureCategory is not null)
            .GroupBy(attempt => attempt.FailureCategory!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReportCounter(
                "cause.failure." + group.Key, group.Key, group.Count(), attempts.Length, ReportCounterKind.Share, null))
            .ToArray();
        if (failures.Length > 0)
        {
            groups.Add(new("causes.failure", "Категории отказов попыток", "доля от попыток модели", failures));
        }

        return groups;
    }

    private static (string Label, string Note) FilterDecisionLabel(string reason) => reason switch
    {
        DvachFilter.Candidate => ("допущен", "фильтр допускает пост к анализу модели"),
        DvachFilter.OpeningPost => ("открывающий пост", "OP треда не рассматривается как анкета"),
        DvachFilter.NoOwnContact => ("нет своего контакта", "контакт не распознан; адресация на это не влияет"),
        DvachFilter.UnresolvedContact => ("возможный контакт не нормализован", "адрес не подтверждён и не поставлен в очередь модели"),
        "" => ("нет данных", "решение фильтра не сохранилось для актуальной ревизии"),
        _ => ("вне контракта", "значение не входит в перечень решений фильтра"),
    };

    private static string AddressingLabel(string addressing) => addressing switch
    {
        "op" => "сам OP",
        "op_reply" => "ответ на OP",
        "none" => "без адресации",
        DvachFilter.OtherReply => "ответ другому посту",
        _ => "нет данных",
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(MomentFormat, CultureInfo.InvariantCulture);

    private static string? Format(DateTimeOffset? value) => value is { } moment ? Format(moment) : null;
}