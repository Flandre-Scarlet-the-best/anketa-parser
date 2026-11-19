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
        Assert.Contains("фильтр: нет своего контакта", html, StringComparison.Ordinal);
        Assert.Contains("адресация: ответ другому посту", html, StringComparison.Ordinal);
        Assert.Contains("адресация — метаданные ответа; причиной отсева она не является", html, StringComparison.Ordinal);
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
        Assert.Contains("фрагмент воспроизведён точно", exactHtml, StringComparison.Ordinal);
        // Подготовленный текст и evidence ссылаются на один и тот же текст ревизии.
        Assert.Contains("реконструкция", exactHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 500, ReportEvidenceView.OutOfRange, "координаты вне текста")]
    [InlineData(-1, 3, ReportEvidenceView.OutOfRange, "координаты вне текста")]
    public void UnverifiableCoordinatesAreShownWithoutHighlight(int start, int length, string status, string label)
    {
        const string text = "Ищу знакомства. Пишите: tg @nick";
        var revision = Revision(text: text);
        var document = Compose(Snapshot([
            Post("100", current: revision, analyses: [Analysis(revision.Id, evidence: new TextSpan(start, length))]),
        ]));

        Assert.Equal(status, Assert.Single(document.Posts).Llm.Decision.Status);
        var html = ReportHtmlRenderer.Render(document);
        Assert.DoesNotContain("<mark>", html, StringComparison.Ordinal);
        Assert.Contains(label, html, StringComparison.Ordinal);
        Assert.Contains("выходят за границы текста", html, StringComparison.Ordinal);
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
    public void InvalidRunMetadataIsReportedWithCategory()
    {
        var path = Path.Combine(Path.GetTempPath(), "anketa-report-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ неверный JSON }", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var error = Assert.Throws<ReportDiagnosticException>(() => ReportRunMetadataReader.Read(path));
            Assert.Equal("run_metadata_invalid", error.Category);
            Assert.Equal(path, Assert.Single(error.Facts).Value);

            var missing = Assert.Throws<ReportDiagnosticException>(
                () => ReportRunMetadataReader.Read(path + ".missing"));
            Assert.Equal("run_metadata_unreadable", missing.Category);
        }
        finally
        {
            File.Delete(path);
        }
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
        File.WriteAllText(
            Path.Combine(directory, "edge-" + scenario + ".html"),
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