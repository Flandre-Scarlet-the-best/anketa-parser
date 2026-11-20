using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using AnketaParser.Cli;
using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Reporting;
using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Tests;

/// <summary>
/// Проверки отчёта: состав снимка, три независимых разреза, честность evidence
/// и безопасность готового HTML. Данные синтетические, без реальных персональных данных.
/// </summary>
public sealed class ReportTests
{
    private const string DefaultText = "Ищу знакомства. Пишите в телеграм.";
    private const string Model = "qwen3.5-9b";

    private static readonly DateTimeOffset Started = new(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 11, 19, 20, 5, 0, TimeSpan.Zero);
    private static readonly Guid SourceId = Guid.Parse("2f4ba4a0-1f6e-4b46-9d60-0d3a0f6a1c11");

    /// <summary>Метка запуска для выгрузки пограничных сценариев: файлы прежних запусков не перезаписываются.</summary>
    private static readonly string EdgeDumpRun = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);

    /// <summary>Признаки активной разметки, которых не может быть в готовом документе.</summary>
    private static readonly string[] ForbiddenActiveMarkup =
    [
        "<script>alert", "<img src=x", "<svg", "<b onclick", "javascript:",
        "innerHTML", "outerHTML", "document.write", "eval(", "srcdoc", "<iframe", "<object", "<embed",
    ];

    /// <summary>Признаки внешних ресурсов: отчёт открывается без сети.</summary>
    private static readonly string[] ForbiddenExternalResources =
    [
        "<link", "@import", "url(", "<img", "<iframe", "<object", "<embed", "srcset", " src=", "<form", "<base",
        "http://", "XMLHttpRequest", "fetch(",
    ];

    [Fact]
    public void MessageContentAndMetadataAreEscapedOnlyAsText()
    {
        const string dangerous = "<script>alert('x')</script>";
        const string attributeBreaker = "\"><img src=x onerror=alert(1)>";
        var revision = Revision(text: dangerous + " " + attributeBreaker, addressing: DvachFilter.OtherReply);
        var post = Post(
            "5959967",
            current: revision,
            contacts:
            [
                new ReportContactFacts("telegram", "<b onclick=\"alert(1)\">@nick</b>", "@nick", 0.9m, "text"),
            ]);
        var document = Compose(
            Snapshot([post]),
            new ReportRunMetadataView("meta.json", true,
                [new ReportMetadataItem("Модель", "<svg onload=alert(1)>")],
                [new ReportMetadataItem("runtime.name", "<svg onload=alert(1)>")]));

        var html = ReportHtmlRenderer.Render(document);

        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;&gt;&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b onclick=&quot;alert(1)&quot;&gt;@nick&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;svg onload=alert(1)&gt;", html, StringComparison.Ordinal);
        foreach (var active in ForbiddenActiveMarkup)
        {
            Assert.DoesNotContain(active, html, StringComparison.OrdinalIgnoreCase);
        }

        // Ни один адрес в готовом документе не выводит за пределы https-ссылки на оригинал
        // или внутреннего перехода по документу.
        var hrefs = Regex.Matches(html, "href=\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(hrefs);
        Assert.All(hrefs, href => Assert.True(
            href.StartsWith("https://", StringComparison.Ordinal) || href.StartsWith('#'),
            href));
    }

    [Theory]
    [InlineData("https://2ch.org/soc/res/5959967.html#5959967")]
    [InlineData("javascript:alert(1)")]
    public void ReportIsSelfContainedAndForbidsActiveMarkup(string postUrl)
    {
        var post = Post("5959967", current: Revision()) with { Url = postUrl };
        var html = ReportHtmlRenderer.Render(Compose(Snapshot([post])));

        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\"", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "<script", RegexOptions.IgnoreCase));
        Assert.Single(Regex.Matches(html, "<style", RegexOptions.IgnoreCase));
        foreach (var external in ForbiddenExternalResources)
        {
            Assert.DoesNotContain(external, html, StringComparison.OrdinalIgnoreCase);
        }

        // Политика содержит хеши ровно тех встроенных блоков, которые есть в документе:
        // хеш считается по тексту элемента, как это делает браузер.
        var style = Regex.Match(html, "<style>(?<body>.*?)</style>", RegexOptions.Singleline).Groups["body"].Value;
        var script = Regex.Match(html, "<script>(?<body>.*?)</script>", RegexOptions.Singleline).Groups["body"].Value;
        Assert.NotEmpty(style);
        Assert.NotEmpty(script);
        Assert.Contains(Hash(style), html, StringComparison.Ordinal);
        Assert.Contains(Hash(script), html, StringComparison.Ordinal);
        Assert.Contains("script-src '" + Hash(script) + "'", html, StringComparison.Ordinal);
        Assert.Contains("style-src '" + Hash(style) + "'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotCompositionIsTheExactSetOfWindowPostNumbers()
    {
        var inside = new[] { Post("100", current: Revision()), Post("200", current: Revision()) };
        var outside = Post("99", current: Revision(receivedAt: Started.AddDays(-1)));
        var document = Compose(Snapshot(inside, [outside]));

        Assert.Equal(inside.Length, document.Posts.Count);
        Assert.Equal(inside.Length, document.Snapshot.IdsInWindow);
        Assert.Equal("100", document.Posts[0].Number);
        Assert.Equal("200", document.Posts[1].Number);
        Assert.Equal("99", Assert.Single(document.PostsOutsideWindow).Number);
        Assert.DoesNotContain("99", document.Posts.Select(post => post.Number));

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("id=\"post-100\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"post-200\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"post-99\"", html, StringComparison.Ordinal);
        Assert.Contains("Посты вне снимка", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsidePostWithoutCurrentRevisionDoesNotBreakReport()
    {
        var inside = Post("100", current: Revision());
        var outside = Post("98") with { CurrentRevision = null, DetailsLoaded = false };
        var document = Compose(Snapshot([inside], [outside]));

        var view = Assert.Single(document.PostsOutsideWindow);
        Assert.Equal(ReportComposer.StageDetailsNotLoaded, view.Stage);
        Assert.Contains("не загружена", view.Text.TextNote, StringComparison.Ordinal);
        Assert.Empty(view.Llm.Decisions);
        // Строгая проверка ревизий основного снимка сохраняется.
        Assert.Throws<ReportDiagnosticException>(() =>
            Compose(Snapshot([Post("97") with { CurrentRevision = null }])));
    }

    [Fact]
    public void PostOutsideWindowDeclaresUnloadedDetailsInsteadOfMissingJobs()
    {
        var inside = Post("100", current: Revision());
        var outside = Post("99", current: Revision(receivedAt: Started.AddDays(-1))) with { DetailsLoaded = false };
        var document = Compose(Snapshot([inside], [outside]));

        var view = Assert.Single(document.PostsOutsideWindow);
        Assert.Equal(ReportComposer.StageDetailsNotLoaded, view.Llm.Status);
        Assert.Contains("не загружены", view.Llm.StatusLabel, StringComparison.Ordinal);
        Assert.Contains("не загружались", view.Llm.Note, StringComparison.Ordinal);
        Assert.Empty(view.Llm.Decisions);
        // Пост внутри окна без задания по-прежнему честно сообщает об отсутствии задания.
        Assert.Equal(ReportComposer.StageNoJob, Assert.Single(document.Posts).Llm.Status);
    }

    [Fact]
    public async Task MetadataWithoutKnownFieldsStillShowsRawValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{"custom":"value"}""", TestContext.Current.CancellationToken);
            var metadata = await ReportRunMetadataReader.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(metadata.Facts);
            var html = ReportHtmlRenderer.Render(Compose(Snapshot([]), metadata));
            // Список исходных полей не скрывается: сводка и он независимы.
            Assert.Contains("Все поля файла метаданных", html, StringComparison.Ordinal);
            Assert.Contains("custom", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MetadataSummarySurvivesDisplayedListTruncation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var numbers = string.Join(',', Enumerable.Range(0, 25_000));
            await File.WriteAllTextAsync(path,
                "{\"steps\":[" + numbers + "],\"modelName\":\"fixture-model\",\"runtime\":\"llama.cpp\"}",
                TestContext.Current.CancellationToken);
            var metadata = await ReportRunMetadataReader.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.True(metadata.Truncated);
            Assert.Equal(200, metadata.All.Count);
            // Сводка не ограничена пределом отображаемого списка: поля находятся
            // и после массива, который целиком в список не попадает.
            Assert.Contains(metadata.Facts,
                item => item.Name.Contains("modelName", StringComparison.Ordinal) && item.Value == "fixture-model");
            Assert.Contains(metadata.Facts,
                item => item.Name.Contains("runtime", StringComparison.Ordinal) && item.Value == "llama.cpp");
            var document = Compose(Snapshot([]), metadata);
            Assert.Contains(document.Notices, notice => notice.Code == "run_metadata_truncated");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3, 2, "snapshot_incomplete_composition")]
    [InlineData(2, 3, "snapshot_contaminated")]
    [InlineData(3, 3, "snapshot_posts_mismatch")]
    public void UnprovableSnapshotFailsWithActualReason(int received, int idsInWindow, string category)
    {
        var inside = new[] { Post("100", current: Revision()), Post("200", current: Revision()) };
        var scan = Scan(received, received, idsInWindow);
        var snapshot = new ReportSnapshot(
            new ReportSourceFacts(SourceId, ThreadUrl, ThreadUrl), scan, inside, []);

        var error = Assert.Throws<ReportDiagnosticException>(() => Compose(snapshot));

        Assert.Equal(category, error.Category);
        // Фактическая причина содержит наблюдаемые значения, а не только код категории.
        Assert.NotEmpty(error.Facts);
        Assert.Contains(error.Facts, fact => fact.Name == "idsInWindow");
    }

    [Fact]
    public void IncompleteScanIsNeverReportedAsSnapshot()
    {
        var scan = Scan(1, 1, 1, status: "failed", phase: "fetch");
        var snapshot = new ReportSnapshot(
            new ReportSourceFacts(SourceId, ThreadUrl, ThreadUrl), scan, [Post("100", current: Revision())], []);

        var error = Assert.Throws<ReportDiagnosticException>(() => Compose(snapshot));

        Assert.Equal("scan_not_complete", error.Category);
        Assert.Contains(error.Facts, fact => fact.Name == "failure");
    }

    [Fact]
    public void EmptySnapshotKeepsCountersConsistentAndSharesUndefined()
    {
        var document = Compose(Snapshot([]));

        Assert.Empty(document.Posts);
        Assert.Equal(0, document.Snapshot.IdsInWindow);
        var stage2 = document.Summary.Single(group => group.Id == "stage2");
        Assert.All(stage2.Counters, counter => Assert.Null(counter.Percent));
        Assert.Equal(0, Counter(document, "stage2", "filter.candidate"));
        Assert.Equal(0, Counter(document, "stage3", "llm.no_result"));

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("Снимок не содержит постов", html, StringComparison.Ordinal);
        Assert.Contains("доля не определена: знаменатель 0", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN %", html, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AllExcludedPostsKeepTheirFilterDecisionAndGiveNoModelDenominator()
    {
        var posts = new[]
        {
            Post("100", current: Revision(reason: DvachFilter.Candidate), excluded: true),
            Post("200", current: Revision(reason: DvachFilter.Candidate), excluded: true),
        };
        var document = Compose(Snapshot(posts));

        Assert.All(document.Posts, post =>
        {
            Assert.True(post.ModerationExcluded);
            Assert.False(post.Filter.AdmittedToLlm);
            Assert.Equal(DvachFilter.Candidate, post.Filter.Reason);
            Assert.Equal(ReportComposer.StageNotAdmitted, post.Stage);
        });
        Assert.Equal(2, Counter(document, "stage2", "moderation.excluded"));
        Assert.Equal(0, Counter(document, "stage2", "filter.admitted"));
        Assert.Null(document.Summary.Single(group => group.Id == "stage3")
            .Counters.Single(counter => counter.Id == "llm.profile").Percent);

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("доля не определена: знаменатель 0", html, StringComparison.Ordinal);
        Assert.Contains("исключён модерацией", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyUncertainDecisionIsSeparatedFromProfileAndNotProfile()
    {
        var revision = Revision();
        var document = Compose(Snapshot([
            Post("100", current: revision, analyses: [Analysis(revision.Id, SemanticContract.Uncertain, "ambiguous")]),
        ]));

        var post = Assert.Single(document.Posts);
        Assert.Equal(ReportComposer.StageUncertain, post.Stage);
        Assert.Equal(1, Counter(document, "stage3", "llm.uncertain"));
        Assert.Equal(0, Counter(document, "stage3", "llm.profile"));
        Assert.Equal(0, Counter(document, "stage3", "llm.not_profile"));
        Assert.Equal(1, Counter(document, "conversion", "conversion.admitted_result"));

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("решение модели: неопределённо", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pending", ReportComposer.StagePending)]
    [InlineData("running", ReportComposer.StageRunning)]
    [InlineData("failed", ReportComposer.StageFailed)]
    public void MissingModelResultIsNeverCountedAsNotProfile(string state, string expectedStage)
    {
        var revision = Revision();
        var document = Compose(Snapshot([
            Post("100", current: revision, jobs: [Job(revision.Id, state, failure: state == "failed" ? "timeout" : null)]),
        ]));

        var post = Assert.Single(document.Posts);
        Assert.Equal(expectedStage, post.Stage);
        Assert.Null(post.Llm.Class);
        Assert.Equal(0, Counter(document, "stage3", "llm.not_profile"));
        Assert.Equal(0, Counter(document, "stage3", "llm.profile"));
        Assert.Equal(1, Counter(document, "stage3", "llm.no_result"));

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("без результата модели", html, StringComparison.Ordinal);
        Assert.Contains("не равно not_profile", html, StringComparison.Ordinal);
        if (state == "failed")
        {
            Assert.Contains("timeout", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PostWithoutModelJobIsNotAdmittedAndHasNoDecision()
    {
        var document = Compose(Snapshot([Post("100", current: Revision())]));

        var post = Assert.Single(document.Posts);
        Assert.Equal(ReportComposer.StageNoJob, post.Stage);
        Assert.True(post.Filter.AdmittedToLlm);
        Assert.Equal(1, Counter(document, "stage3", "llm.no_job"));
        Assert.Equal(0, Counter(document, "stage3", "llm.not_profile"));
    }

    [Fact]
    public void PendingQueueJobWithoutPipelineVersionIsNotMissingJob()
    {
        // Строка очереди создаётся вместе с допуском к анализу, а версия конвейера
        // проставляется только при захвате: ожидающее задание не имеет LlmVersion,
        // но это задание модели, а не отсутствие задания.
        var revision = Revision();
        var document = Compose(Snapshot([
            Post("100", current: revision, jobs: [Job(revision.Id, "pending", llmVersion: null)]),
        ]));

        var post = Assert.Single(document.Posts);
        Assert.Equal(ReportComposer.StagePending, post.Stage);
        Assert.Equal(1, Counter(document, "stage3", "llm.pending"));
        Assert.Equal(0, Counter(document, "stage3", "llm.no_job"));
        Assert.Equal(0, Counter(document, "stage3", "llm.not_profile"));
        var job = Assert.Single(post.Llm.Jobs);
        Assert.Equal("llm", job.Kind);
        Assert.Equal("задание модели: ожидает захвата", job.KindLabel);
        Assert.Null(job.LlmVersion);

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("задание модели: ожидает захвата", html, StringComparison.Ordinal);
        Assert.DoesNotContain("задание сборщика", html, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleRevisionIsExcludedFromSummaryAndMarkedInDetail()
    {
        var currentRevision = Revision(hash: "hash-current");
        var staleRevision = Revision(hash: "hash-stale", receivedAt: Started.AddDays(-2));
        var post = Post(
            "100",
            current: currentRevision,
            stale: [staleRevision],
            analyses: [Analysis(staleRevision.Id, SemanticContract.Profile, "invitation")]);
        var document = Compose(Snapshot([post]));

        var view = Assert.Single(document.Posts);
        Assert.Equal(ReportComposer.StageStaleOnly, view.Stage);
        Assert.Null(view.Llm.Class);
        Assert.Equal(0, Counter(document, "stage3", "llm.profile"));
        Assert.Equal(1, Counter(document, "stage1", "revision.stale"));
        Assert.Equal(1, Counter(document, "stage1", "revision.stale_posts"));
        Assert.Equal(2, view.Revisions.Count);
        Assert.Single(view.Revisions, revision => revision.Current);
        Assert.Single(view.Revisions, revision => !revision.Current && revision.Id == staleRevision.Id.ToString("D", CultureInfo.InvariantCulture));
        var decision = Assert.Single(view.Llm.Decisions);
        Assert.False(decision.Current);
        Assert.Equal(staleRevision.Id.ToString("D", CultureInfo.InvariantCulture), decision.RevisionId);

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("результат только у устаревшей ревизии", html, StringComparison.Ordinal);
        Assert.Contains("устаревшая", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherReplyStaysAddressingAndNeverBecomesExclusionCause()
    {
        var document = Compose(Snapshot([
            Post("100", current: Revision(reason: DvachFilter.NoOwnContact, addressing: DvachFilter.OtherReply)),
        ]));

        var post = Assert.Single(document.Posts);
        Assert.Equal(DvachFilter.NoOwnContact, post.Filter.Reason);
        Assert.Equal(DvachFilter.OtherReply, post.Filter.Addressing);
        Assert.Equal("ответ другому посту", post.Filter.AddressingLabel);
        Assert.DoesNotContain("исключ", post.Filter.AddressingNote, StringComparison.OrdinalIgnoreCase);

        var causes = document.Summary.Single(group => group.Id == "causes.filter");
        Assert.Equal(1, causes.Counters.Single(counter => counter.Id == "cause.no_own_contact").Value);
        Assert.DoesNotContain(causes.Counters, counter => counter.Id.Contains("other_reply", StringComparison.Ordinal));
        var addressing = document.Summary.Single(group => group.Id == "stage2")
            .Counters.Single(counter => counter.Id == "addressing.other_reply");
        Assert.Equal(1, addressing.Value);
        Assert.Equal("адресация, а не причина отсева", addressing.Note);

        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("решение фильтра: нет своего контакта", html, StringComparison.Ordinal);
        Assert.Contains("адресация: ответ другому посту", html, StringComparison.Ordinal);
        Assert.Contains("адресация — метаданные ответа; причиной отсева она не является", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedContactHasItsOwnCounterAndIsNotAdmitted()
    {
        var document = Compose(Snapshot([
            Post("100", current: Revision(reason: DvachFilter.UnresolvedContact)),
        ]));
        var post = Assert.Single(document.Posts);
        Assert.False(post.Filter.AdmittedToLlm);
        Assert.Equal(1, Counter(document, "stage2", "filter.unresolved_contact"));
        Assert.Equal(0, Counter(document, "stage2", "filter.no_own_contact"));
        Assert.Equal(0, Counter(document, "stage2", "filter.unknown"));
        Assert.Contains("возможный контакт не нормализован", ReportHtmlRenderer.Render(document), StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceIsHighlightedOnlyWhenReproducedExactly()
    {
        const string text = "Ищу знакомства. Пишите: tg @nick";
        var revision = Revision(text: text);
        var exact = Compose(Snapshot([
            Post("100", current: revision, analyses: [Analysis(revision.Id, evidence: new TextSpan(0, 14))]),
        ]));
        var exactHtml = ReportHtmlRenderer.Render(exact);
        Assert.Equal(ReportEvidenceView.Exact, Assert.Single(exact.Posts).Llm.Decision.Status);
        Assert.Contains("<mark>Ищу знакомства</mark>", exactHtml, StringComparison.Ordinal);
        // Формулировка точного воспроизведения печатается один раз: подпись статуса и пояснение
        // не дублируют друг друга, а координаты остаются рядом.
        Assert.Contains(
            "<p class=\"note\">фрагмент восстановлен по сохранённым координатам точно. Координаты UTF-16: 0+14.</p>",
            exactHtml,
            StringComparison.Ordinal);
        Assert.Contains("фрагмент восстановлен по сохранённым координатам точно", exactHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("воспроизведён точно: фрагмент восстановлен", exactHtml, StringComparison.Ordinal);
        // Подготовленный текст и evidence ссылаются на один и тот же текст ревизии.
        Assert.Contains("реконструкция", exactHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldLabelIsShownOnlyWhenItsRawValueIsVerified()
    {
        const string text = "Живу в ДС-1, ищу компанию.";
        var revision = Revision(text: text);
        var analysis = Analysis(revision.Id, fields: [new("location", new TextSpan(0, "Живу в ДС-1".Length), "Москва")])
            with
        { Location = "ДС-1" };
        var document = Compose(Snapshot([Post("100", current: revision, analyses: [analysis])]));
        var field = Assert.Single(Assert.Single(document.Posts).Llm.Fields);
        Assert.Equal("Москва", field.Value);
        Assert.Equal("Живу в ДС-1", field.Evidence.Fragment);
        Assert.Equal(ReportEvidenceView.Exact, field.Evidence.Status);

        var inconsistent = Compose(Snapshot([Post("100", current: revision, analyses: [analysis with { Location = "ДС-2" }])]));
        field = Assert.Single(Assert.Single(inconsistent.Posts).Llm.Fields);
        Assert.Equal("ДС-2", field.Value);
        Assert.Equal(ReportEvidenceView.ValueNotInSpan, field.Evidence.Status);
    }

    [Theory]
    [InlineData(0, 500, ReportEvidenceView.OutOfRange)]
    [InlineData(-1, 3, ReportEvidenceView.OutOfRange)]
    [InlineData(int.MaxValue, 1, ReportEvidenceView.OutOfRange)]
    public void UnverifiableCoordinatesAreShownWithoutHighlight(int start, int length, string status)
    {
        const string text = "Ищу знакомства. Пишите: tg @nick";
        var revision = Revision(text: text);
        var document = Compose(Snapshot([
            Post("100", current: revision, analyses: [Analysis(revision.Id, evidence: new TextSpan(start, length))]),
        ]));

        Assert.Equal(status, Assert.Single(document.Posts).Llm.Decision.Status);
        var html = ReportHtmlRenderer.Render(document);
        Assert.DoesNotContain("<mark>", html, StringComparison.Ordinal);
        Assert.Contains("выходят за границы текста", html, StringComparison.Ordinal);
        // Пояснение не повторяет подпись статуса: дублирование формулировок исключено.
        Assert.DoesNotContain("координаты вне текста: координаты", html, StringComparison.Ordinal);
        Assert.Contains("Подсветка не выполняется: точное воспроизведение не подтверждено.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskedFragmentIsNotHighlightedAndTextlessRevisionIsExplicit()
    {
        const string text = "Ищу знакомства. Контакт: ██████.";
        var revision = Revision(text: text);
        var masked = Compose(Snapshot([
            Post("100", current: revision, analyses: [Analysis(revision.Id, evidence: new TextSpan(25, 6))]),
        ]));
        Assert.Equal(ReportEvidenceView.MaskChar, Assert.Single(masked.Posts).Llm.Decision.Status);
        var maskedHtml = ReportHtmlRenderer.Render(masked);
        Assert.DoesNotContain("<mark>", maskedHtml, StringComparison.Ordinal);
        Assert.Contains("фрагмент содержит символ маски", maskedHtml, StringComparison.Ordinal);

        var textlessRevision = Revision(text: null);
        var textless = Compose(Snapshot([
            Post("100", current: textlessRevision, analyses: [Analysis(textlessRevision.Id, evidence: new TextSpan(0, 5))]),
        ]));
        Assert.Equal(ReportEvidenceView.NoText, Assert.Single(textless.Posts).Llm.Decision.Status);
        var textlessHtml = ReportHtmlRenderer.Render(textless);
        Assert.DoesNotContain("<mark>", textlessHtml, StringComparison.Ordinal);
        Assert.Contains("текст ревизии недоступен", textlessHtml, StringComparison.Ordinal);
        Assert.Contains("Точное воспроизведение невозможно", textlessHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedCardCannotWidenTheGridAndMetadataPathIsShortened()
    {
        const string metadataPath = @"C:\Users\example\run\run-metadata.json";
        var revision = Revision();
        var document = Compose(
            Snapshot([Post("100", current: revision, analyses: [Analysis(revision.Id)])]),
            new ReportRunMetadataView(
                metadataPath,
                true,
                [new ReportMetadataItem("Модель (model.name)", "Qwen")],
                [new ReportMetadataItem("model.name", "Qwen")]));

        var html = ReportHtmlRenderer.Render(document);

        // Причина дефекта: grid-элемент не сжимался ниже min-content ширины таблиц, поэтому
        // раскрытая карточка расширяла сетку и появлялась горизонтальная прокрутка страницы.
        Assert.Matches("article\\.post \\{[^}]*min-width: 0", html);
        Assert.Matches("\\.post-head \\{[^}]*min-width: 0", html);

        // В шапке — только имя файла, полный путь остаётся в подсказке и не теряется.
        Assert.Contains(">run-metadata.json</span>", html, StringComparison.Ordinal);
        Assert.Contains("title=\"" + metadataPath + "\"", html, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, Regex.Escape(metadataPath)));
    }

    [Theory]
    [InlineData(@"C:\Users\example\run\run-metadata.json")]
    [InlineData("/home/example/run/run-metadata.json")]
    [InlineData("run-metadata.json")]
    public void MetadataFileNameIsShortenedRegardlessOfSeparator(string metadataPath)
    {
        // Отчёт переносим: путь мог быть записан в Windows-окружении и отрисован в Linux.
        var revision = Revision();
        var document = Compose(
            Snapshot([Post("100", current: revision, analyses: [Analysis(revision.Id)])]),
            new ReportRunMetadataView(metadataPath, true, [], []));
        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains(">run-metadata.json</span>", html, StringComparison.Ordinal);
        Assert.Contains("title=\"" + metadataPath + "\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BadgesStayUnambiguousForModerationExcludedPost()
    {
        var document = Compose(Snapshot([
            Post("100", current: Revision(reason: DvachFilter.Candidate), excluded: true),
        ]));
        var post = Assert.Single(document.Posts);
        Assert.True(post.ModerationExcluded);
        Assert.False(post.Filter.AdmittedToLlm);

        var html = ReportHtmlRenderer.Render(document);

        // Итог этапа называет причину недопуска, а бейдж решения фильтра описывает только фильтр:
        // иначе «допущен» рядом с «не допущен» читалось бы двусмысленно.
        Assert.Contains("не допущен к анализу модели: исключён модерацией", html, StringComparison.Ordinal);
        Assert.Contains("решение фильтра: допущен", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SharesAreMarkedAsRoundedWithoutChangingValues()
    {
        var document = Compose(Snapshot([Post("100", current: Revision())]));

        var html = ReportHtmlRenderer.Render(document);

        Assert.Contains("Доли округлены до 0,1 %", html, StringComparison.Ordinal);
        Assert.Contains("100.0 %", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedTextReconstructionMasksContactsAndSaysSo()
    {
        const string text = "Пишите: tg @nick";
        var revision = Revision(text: text, contacts: 1);
        var document = Compose(Snapshot([
            Post(
                "100",
                current: revision,
                contacts: [new ReportContactFacts("telegram", "tg @nick", "@nick", 0.9m, "text")]),
        ]));

        var post = Assert.Single(document.Posts);
        Assert.Equal("reconstructed", post.Text.PreparedStatus);
        Assert.Equal("Пишите: ████████", post.Text.PreparedText);
        var html = ReportHtmlRenderer.Render(document);
        Assert.Contains("Подготовленный текст (реконструкция)", html, StringComparison.Ordinal);
        Assert.Contains("████████", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRunMetadataIsReportedWithCategory()
    {
        var path = Path.Combine(Path.GetTempPath(), "anketa-report-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ неверный JSON }", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var error = await Assert.ThrowsAsync<ReportDiagnosticException>(() => ReportRunMetadataReader.ReadAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("run_metadata_invalid", error.Category);
            Assert.Equal(path, Assert.Single(error.Facts).Value);

            var missing = await Assert.ThrowsAsync<ReportDiagnosticException>(
                () => ReportRunMetadataReader.ReadAsync(path + ".missing", TestContext.Current.CancellationToken));
            Assert.Equal("run_metadata_unreadable", missing.Category);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MetadataPathsDistinguishDottedKeysAndNestedObjects()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{"model.name":"A","model":{"name":"B"},"temperature":0.5}""", TestContext.Current.CancellationToken);
            var metadata = await ReportRunMetadataReader.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(metadata.All, item => item.Name == "[\"model.name\"]" && item.Value == "A");
            Assert.Contains(metadata.All, item => item.Name == "model.name" && item.Value == "B");
            Assert.Contains(metadata.Facts, item => item.Name.Contains("model.name", StringComparison.Ordinal) && item.Value == "B");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CancelledReportWritePreservesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".html");
        try
        {
            await File.WriteAllTextAsync(path, "предыдущий отчёт", TestContext.Current.CancellationToken);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportCommands.WriteHtmlAsync(path, "новый отчёт", cancelled.Token));
            Assert.Equal("предыдущий отчёт", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            await ReportCommands.WriteHtmlAsync(path, "новый отчёт", TestContext.Current.CancellationToken);
            Assert.Equal("новый отчёт", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MetadataReadHonorsCancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportRunMetadataReader.ReadAsync(path, cancelled.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CurrentJobWinsOverStaleDecisionAndStaleJob()
    {
        var current = Revision();
        var stale = Revision();
        var oldJob = Job(stale.Id, "failed", "timeout") with { AvailableAt = Started.AddDays(2) };
        var currentJob = Job(current.Id, "pending") with { AvailableAt = Started };
        var document = Compose(Snapshot([Post("100", current: current, stale: [stale],
            analyses: [Analysis(stale.Id)], jobs: [oldJob, currentJob])]));
        var post = Assert.Single(document.Posts);
        Assert.Equal(ReportComposer.StagePending, post.Stage);
        Assert.Equal("pending", post.Llm.CurrentJobState);
        Assert.Collection(post.Llm.Jobs, job => Assert.Equal("pending", job.State), job => Assert.Equal("failed", job.State));
        Assert.Contains("data-job=\"pending\"", ReportHtmlRenderer.Render(document), StringComparison.Ordinal);
    }

    [Fact]
    public void JobsAreSortedBeforeDatesAreFormatted()
    {
        var revision = Revision();
        var october = Job(revision.Id, "pending") with { AvailableAt = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero) };
        var november = Job(revision.Id, "failed", "timeout") with { AvailableAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        var post = Assert.Single(Compose(Snapshot([Post("100", current: revision, jobs: [november, october])])).Posts);
        Assert.Collection(post.Llm.Jobs, job => Assert.Equal("pending", job.State), job => Assert.Equal("failed", job.State));
        Assert.Equal("failed", post.Llm.CurrentJobState);
    }

    [Fact]
    public void EvidenceCrossingContactMaskCannotHighlightOriginalContact()
    {
        const string text = "Давай общаться, @sampleuser42";
        var revision = Revision(text: text, contacts: 1);
        var document = Compose(Snapshot([Post("100", current: revision,
            analyses: [Analysis(revision.Id, evidence: new TextSpan(0, text.Length))],
            contacts: [new ReportContactFacts("telegram", "@sampleuser42", "sampleuser42", 0.9m, "text")])]));
        var evidence = Assert.Single(document.Posts).Llm.Decision;
        Assert.Equal(ReportEvidenceView.MaskChar, evidence.Status);
        Assert.Null(evidence.Span);
        Assert.DoesNotContain("sampleuser42", evidence.Fragment!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Пограничные состояния, которых нет в реальных данных: пустой снимок, все посты
    /// исключены фильтром или модерацией, только неопределённые решения, только ожидающие
    /// задания, только технические отказы. Отчёт не должен терять посты, показывать долю
    /// при нулевом знаменателе или переставать быть автономным.
    /// </summary>
    [Theory]
    [InlineData("empty", 0, null, 0)]
    [InlineData("all_excluded", 2, ReportComposer.StageNotAdmitted, 0)]
    [InlineData("only_uncertain", 2, SemanticContract.Uncertain, 2)]
    [InlineData("only_pending", 2, ReportComposer.StagePending, 2)]
    [InlineData("only_failed", 2, ReportComposer.StageFailed, 2)]
    public void EdgeSnapshotScenariosStayConsistentAndSelfContained(
        string scenario, int expectedPosts, string? expectedStage, int expectedAdmitted)
    {
        var document = Compose(Snapshot(EdgePosts(scenario)));

        Assert.Equal(expectedPosts, document.Posts.Count);
        Assert.Equal(expectedAdmitted, Counter(document, "stage2", "filter.admitted"));
        if (expectedStage is not null)
        {
            Assert.All(document.Posts, post => Assert.Equal(expectedStage, post.Stage));
        }

        // Доли считаются только при положительном знаменателе: иначе значение не определено.
        foreach (var counter in document.Summary.SelectMany(group => group.Counters))
        {
            if (counter.Kind != ReportCounterKind.Share)
            {
                continue;
            }

            if (counter.Total is > 0)
            {
                Assert.NotNull(counter.Percent);
                Assert.InRange(counter.Percent!.Value, 0m, 100m);
            }
            else
            {
                Assert.Null(counter.Percent);
            }
        }

        // Ни один счётчик-количество не может быть отрицательным, а посты снимка
        // распределяются по стадиям без потерь.
        Assert.All(document.Summary.SelectMany(group => group.Counters),
            counter => Assert.True(counter.Value >= 0, counter.Id));
        Assert.Equal(expectedPosts, document.Summary
            .Single(group => group.Id == "stage1")
            .Counters.Single(counter => counter.Id == "snapshot.posts").Value);

        var html = ReportHtmlRenderer.Render(document);
        Assert.Equal(expectedPosts, Regex.Count(html, "class=\"post\""));
        Assert.All(document.Posts, post => Assert.Contains(EscapeHtmlText(post.Number), html, StringComparison.Ordinal));
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\"", html, StringComparison.Ordinal);
        foreach (var external in ForbiddenExternalResources)
        {
            Assert.DoesNotContain(external, html, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var active in ForbiddenActiveMarkup)
        {
            Assert.DoesNotContain(active, html, StringComparison.OrdinalIgnoreCase);
        }

        // Сценарий не должен показывать вычисленную долю там, где знаменателя нет:
        // в разметке нет ни «NaN %», ни бесконечности (isNaN во встроенном сценарии — не текст отчёта).
        Assert.DoesNotContain("NaN %", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN%", html, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", html, StringComparison.Ordinal);
        var style = Regex.Match(html, "<style>(?<body>.*?)</style>", RegexOptions.Singleline).Groups["body"].Value;
        var script = Regex.Match(html, "<script>(?<body>.*?)</script>", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("script-src '" + Hash(script) + "'", html, StringComparison.Ordinal);
        Assert.Contains("style-src '" + Hash(style) + "'", html, StringComparison.Ordinal);

        DumpEdgeScenario(scenario, html);
    }

    [Fact]
    public void EvidenceCoordinatesDropDigitGroupingWhileStatisticsKeepIt()
    {
        // Смещение и длина — это пара чисел: «2128+134» читается однозначно, «2,128+134» — нет.
        // Текст собран из повторяющихся фраз, а не из одного символа: так он остаётся похожим
        // на реальный и не провоцирует катастрофический откат в разборщиках содержимого.
        var text = string.Concat(Enumerable.Repeat("Ищу знакомства. Пишите в телеграм. ", 80));
        var revision = Revision(text: text);
        var attempt = new ReportAttemptFacts(
            Number: 1,
            StartedAt: Started.AddMinutes(2),
            FinishedAt: Started.AddMinutes(3),
            ElapsedMilliseconds: 3_855,
            MeasuredRequests: 1_073,
            ReservedRequests: 1_230,
            InputTokens: 654_002,
            OutputTokens: 118_095,
            CachedInputTokens: 523_892,
            FailureCategory: null);
        var job = new ReportJobFacts(
            Guid.NewGuid(),
            revision.Id,
            DvachFilter.ProcessorVersion,
            "profile-analysis-v1",
            "succeeded",
            1,
            DvachFilter.MaxAttempts,
            Started.AddMinutes(2),
            null,
            [attempt]);
        var document = Compose(Snapshot([
            Post("100", current: revision, jobs: [job], analyses: [Analysis(revision.Id, evidence: new TextSpan(2128, 134))]),
        ]));

        var html = ReportHtmlRenderer.Render(document);

        Assert.Equal(ReportEvidenceView.Exact, Assert.Single(document.Posts).Llm.Decision.Status);
        Assert.Contains("Координаты UTF-16: 2128+134.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("2,128+134", html, StringComparison.Ordinal);
        // Статистика сохраняет группировку разрядов: токены, время попытки и число запросов.
        Assert.Contains("654,002", html, StringComparison.Ordinal);
        Assert.Contains("3,855", html, StringComparison.Ordinal);
        Assert.Contains("1,073", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScreensDetachToolbarAndAnchorsReserveItsHeight()
    {
        var document = Compose(Snapshot([Post("100", current: Revision())]));

        var html = ReportHtmlRenderer.Render(document);
        var style = Regex.Match(html, "<style>(?<body>.*?)</style>", RegexOptions.Singleline).Groups["body"].Value;
        var script = Regex.Match(html, "<script>(?<body>.*?)</script>", RegexOptions.Singleline).Groups["body"].Value;

        // На узких экранах панель не закреплена и её отступы уменьшены.
        Assert.Contains("@media (max-width: 640px)", style, StringComparison.Ordinal);
        Assert.Matches("@media \\(max-width: 640px\\) \\{[^}]*\\.toolbar \\{ position: static;", style);
        Assert.Contains("@media (max-height: 480px)", style, StringComparison.Ordinal);

        // Переход по якорю учитывает фактическую высоту панели и не создаёт пустоту без sticky.
        Assert.Contains(
            "article.post, section[id] { scroll-margin-top: calc(var(--toolbar-offset, 0px) + 8px); }",
            style,
            StringComparison.Ordinal);
        Assert.Contains("--toolbar-offset", script, StringComparison.Ordinal);
        Assert.Contains("getComputedStyle(toolbar).position === 'sticky'", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('resize', syncToolbarOffset)", script, StringComparison.Ordinal);
        // Рендерер считает хеши сам: в разметке стоят хеши ровно этого сценария и этих стилей.
        Assert.Contains("script-src '" + Hash(script) + "'", html, StringComparison.Ordinal);
        Assert.Contains("style-src '" + Hash(style) + "'", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Снимок нельзя доказать — отчёт не строится. Проверяются расхождение
    /// <c>Received</c>/<c>Applied</c>, незакрытый журнал, отсутствие границ окна,
    /// пост без текущей ревизии и ревизия, полученная позже окончания скана.
    /// </summary>
    [Theory]
    [InlineData("received_not_applied", "scan_partially_applied")]
    [InlineData("journal_pending", "scan_journal_pending")]
    [InlineData("bounds_missing", "scan_bounds_missing")]
    [InlineData("post_without_revision", "post_revision_missing")]
    [InlineData("revision_after_scan", "revision_after_scan")]
    public void UnprovableScanStopsTheReportWithActualFacts(string scenario, string expectedCategory)
    {
        var posts = new[] { Post("100", current: Revision()) };
        var scan = scenario switch
        {
            "received_not_applied" => Scan(applied: 1, received: 2, idsInWindow: 2),
            "journal_pending" => Scan(applied: 1, received: 1, idsInWindow: 1, journalPending: true),
            "bounds_missing" => Scan(applied: 1, received: 1, idsInWindow: 1) with { StartedAt = null },
            "post_without_revision" => Scan(applied: 1, received: 1, idsInWindow: 1),
            _ => Scan(applied: 1, received: 1, idsInWindow: 1),
        };
        if (scenario == "post_without_revision")
        {
            posts = [posts[0] with { CurrentRevisionId = null, CurrentRevision = null }];
        }

        if (scenario == "revision_after_scan")
        {
            posts = [Post("100", current: Revision(receivedAt: Finished.AddMinutes(5)))];
        }

        var error = Assert.Throws<ReportDiagnosticException>(() => SnapshotBoundary.Validate(scan, posts));
        Assert.Equal(expectedCategory, error.Category);
        Assert.NotEmpty(error.Facts);
        Assert.All(error.Facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.Value)));
        if (scenario == "received_not_applied")
        {
            Assert.Contains(error.Facts, fact => fact.Name == "received" && fact.Value == "2");
            Assert.Contains(error.Facts, fact => fact.Name == "applied" && fact.Value == "1");
        }

        if (scenario == "revision_after_scan")
        {
            Assert.Contains(error.Facts, fact => fact.Name == "posts" && fact.Value == "100");
        }
    }

    private const string ThreadUrl = "https://2ch.org/soc/res/5959967.html";

    /// <summary>Посты пограничных состояний снимка для проверок рендера и счётчиков.</summary>
    private static ReportPostFacts[] EdgePosts(string scenario)
    {
        static ReportPostFacts WithAnalysis(string number, string semanticClass)
        {
            var revision = Revision();
            return Post(number, current: revision, analyses: [Analysis(revision.Id, semanticClass)]);
        }

        static ReportPostFacts WithJob(string number, string state, string? failure = null, string? llmVersion = null)
        {
            var revision = Revision();
            return Post(number, current: revision, jobs: [Job(revision.Id, state, failure, llmVersion)]);
        }

        return scenario switch
        {
            "empty" => [],
            "all_excluded" =>
            [
                Post("100", current: Revision(reason: DvachFilter.Candidate), excluded: true),
                Post("200", current: Revision(reason: DvachFilter.Candidate), excluded: true),
            ],
            "only_uncertain" =>
            [
                WithAnalysis("100", SemanticContract.Uncertain),
                WithAnalysis("200", SemanticContract.Uncertain),
            ],
            "only_pending" => [WithJob("100", "pending"), WithJob("200", "pending")],
            "only_failed" => [WithJob("100", "failed", "timeout"), WithJob("200", "failed", "invalid_output_schema")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Неизвестный пограничный сценарий."),
        };
    }

    /// <summary>Сохраняет HTML пограничного сценария, если задан каталог для локальной проверки.</summary>
    private static void DumpEdgeScenario(string scenario, string html)
    {
        var directory = Environment.GetEnvironmentVariable("ANKETAPARSER_REPORT_EDGE_DUMP");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        // Отдельный каталог запуска: сохранённый результат прежней проверки
        // и ручные файлы в указанном каталоге остаются неизменными.
        var runDirectory = Path.Combine(directory, "run-" + EdgeDumpRun);
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(
            Path.Combine(runDirectory, "edge-" + scenario + ".html"),
            html,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string EscapeHtmlText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static ReportDocument Compose(
        ReportSnapshot snapshot, ReportRunMetadataView? metadata = null) =>
        ReportComposer.Build(snapshot, metadata, "0.0.0-test", Finished.AddMinutes(1));

    private static ReportSnapshot Snapshot(
        ReportPostFacts[] posts, ReportPostFacts[]? outside = null) =>
        new(
            new ReportSourceFacts(SourceId, ThreadUrl, ThreadUrl),
            Scan(posts.Length, posts.Length, posts.Length, outsideCount: outside?.Length ?? 0),
            posts,
            outside ?? []);

    private static ReportScanFacts Scan(
        int applied,
        int received,
        int idsInWindow,
        int outsideCount = 0,
        string status = "complete",
        string phase = "complete",
        bool journalPending = false) =>
        new(
            status,
            phase,
            status == "complete" ? null : "fixture_failure",
            Started,
            Finished,
            Finished,
            applied,
            journalPending,
            received,
            false,
            idsInWindow,
            outsideCount,
            new DvachScanMetrics(Received: received, Closed: false, Candidates: received));

    private static ReportRevisionFacts Revision(
        string hash = "hash",
        string? reason = null,
        string? addressing = "op",
        string? text = DefaultText,
        int contacts = 0,
        bool ambiguous = false,
        DateTimeOffset? receivedAt = null) =>
        new(
            Guid.NewGuid(),
            hash,
            receivedAt ?? Started.AddMinutes(-1),
            reason ?? DvachFilter.Candidate,
            addressing,
            contacts,
            ambiguous,
            text,
            [],
            []);

    private static ReportPostFacts Post(
        string number,
        ReportRevisionFacts? current = null,
        bool excluded = false,
        IReadOnlyList<ReportRevisionFacts>? stale = null,
        IReadOnlyList<ReportAnalysisFacts>? analyses = null,
        IReadOnlyList<ReportJobFacts>? jobs = null,
        IReadOnlyList<ReportContactFacts>? contacts = null)
    {
        var revision = current ?? Revision();
        List<ReportRevisionFacts> revisions = [revision];
        if (stale is not null)
        {
            revisions.AddRange(stale);
        }

        return new ReportPostFacts(
            Guid.NewGuid(),
            number,
            string.Create(CultureInfo.InvariantCulture, $"https://2ch.org/soc/res/5959967.html#{number}"),
            Started.AddHours(-1),
            Started.AddMinutes(1),
            excluded,
            revision.Id,
            revision,
            revisions,
            jobs ?? [],
            analyses ?? [],
            contacts ?? []);
    }

    private static ReportAnalysisFacts Analysis(
        Guid revisionId,
        string semanticClass = SemanticContract.Profile,
        string? reason = "invitation",
        TextSpan? evidence = null,
        IReadOnlyList<ReportFieldSpan>? fields = null,
        IReadOnlyList<string>? unsupported = null,
        ProfileState? profileState = null) =>
        new(
            Guid.NewGuid(),
            revisionId,
            semanticClass,
            reason,
            Model,
            "local-llm",
            "profile-analysis-v1",
            SemanticContract.SchemaVersion,
            30,
            "Москва",
            "женщина",
            "кино",
            "знакомство",
            fields ?? [],
            unsupported ?? [],
            null,
            null,
            evidence,
            false,
            Started.AddMinutes(1),
            profileState is { } state
                ? new ReportProfileFacts(Guid.NewGuid(), state, Started.AddMinutes(1))
                : null);

    private static ReportJobFacts Job(
        Guid revisionId, string state, string? failure = null, string? llmVersion = "profile-analysis-v1") =>
        new(
            Guid.NewGuid(),
            revisionId,
            DvachFilter.ProcessorVersion,
            llmVersion,
            state,
            failure is null ? 0 : 1,
            DvachFilter.MaxAttempts,
            Started.AddMinutes(2),
            failure,
            failure is null ? [] : [Attempt(failure)]);

    private static ReportAttemptFacts Attempt(string? failure) =>
        new(
            1,
            Started.AddMinutes(2),
            Started.AddMinutes(3),
            1_200,
            1,
            0,
            1_000,
            200,
            0,
            failure);

    private static long Counter(ReportDocument document, string groupId, string counterId) =>
        document.Summary.Single(group => group.Id == groupId)
            .Counters.Single(counter => counter.Id == counterId).Value;

    private static string Hash(string content) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}