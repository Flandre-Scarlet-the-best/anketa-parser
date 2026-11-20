using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using AnketaParser.Infrastructure.Reporting;

namespace AnketaParser.Cli;

/// <summary>
/// Рендерер автономного интерактивного HTML-отчёта. Один файл без внешних ресурсов:
/// стили и сценарий встроены, политика безопасности задана через <c>meta</c> с хешами
/// встроенных блоков, а всё содержимое сообщений выводится только экранированным текстом.
/// </summary>
public static class ReportHtmlRenderer
{
    private const string MomentFormat = "yyyy-MM-dd HH:mm:ss'Z'";

    /// <summary>Политика безопасности: ни сети, ни активной разметки, только встроенные блоки по хешу.</summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src '{0}'; style-src '{1}'; img-src data:; font-src 'none'; " +
        "media-src 'none'; object-src 'none'; frame-src 'none'; connect-src 'none'; form-action 'none'; base-uri 'none'";

    private const string StyleSheet = """
        :root {
          color-scheme: light dark;
          --bg: #f4f6f9; --panel: #ffffff; --ink: #16191d; --muted: #5b6472; --line: #d7dde6;
          --pos: #1d7a46; --neg: #a3372a; --warn: #8a6400; --bad: #a01c1c; --wait: #255fa8; --idle: #5b6472;
          --accent: #24507d; --mark: #ffefb8;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #12161a; --panel: #191e24; --ink: #e7ecf2; --muted: #9aa6b4; --line: #2b333d;
            --pos: #57c98a; --neg: #ef9184; --warn: #e0c272; --bad: #f28b8b; --wait: #86b6ef; --idle: #9aa6b4;
            --accent: #9dc4ec; --mark: #6b5310;
          }
        }
        * { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; }
        body { margin: 0; background: var(--bg); color: var(--ink);
          font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
        .wrap { max-width: 1560px; margin: 0 auto; padding: 14px; }
        h1 { font-size: 1.4rem; margin: 0 0 4px; line-height: 1.25; }
        h2 { font-size: 1.1rem; margin: 0 0 6px; line-height: 1.3; }
        h3 { font-size: 0.98rem; margin: 14px 0 6px; }
        p { margin: 0 0 8px; }
        a { color: var(--accent); }
        .sub { color: var(--muted); font-size: 0.88rem; margin: 0 0 10px; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 12px; margin: 0 0 14px; }
        .jump { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 14px; }
        .jump a { display: inline-block; padding: 5px 9px; border: 1px solid var(--line); border-radius: 999px;
          background: var(--panel); font-size: 0.82rem; text-decoration: none; }
        .jump a:hover, .jump a:focus-visible { border-color: var(--accent); }
        .scroll { overflow-x: auto; }
        table.data { width: 100%; border-collapse: collapse; font-size: 0.83rem; }
        table.data caption { text-align: left; color: var(--muted); font-size: 0.8rem; padding: 0 0 4px; }
        table.data th, table.data td { border-bottom: 1px solid var(--line); padding: 5px 7px; text-align: left; vertical-align: top; }
        table.data thead th { color: var(--muted); font-weight: 600; white-space: nowrap; }
        table.data td.num, table.data th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        table.data td.wrap-cell { overflow-wrap: anywhere; }
        .note { color: var(--muted); font-size: 0.8rem; }
        .notices { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
        .notices li { border-left: 3px solid var(--idle); padding: 5px 9px; background: rgba(91, 100, 114, 0.08); border-radius: 4px; font-size: 0.85rem; }
        .notices li.limitation { border-left-color: var(--warn); }
        .notices li.warning { border-left-color: var(--bad); }
        .badge { display: inline-block; padding: 1px 7px; border-radius: 999px; font-size: 0.75rem; line-height: 1.5;
          border: 1px solid var(--line); color: var(--muted); background: transparent; white-space: nowrap; }
        .badge.solid { color: #fff; border-color: transparent; }
        .tone-pos { --tone: var(--pos); }
        .tone-neg { --tone: var(--neg); }
        .tone-warn { --tone: var(--warn); }
        .tone-bad { --tone: var(--bad); }
        .tone-wait { --tone: var(--wait); }
        .tone-idle { --tone: var(--idle); }
        .badge.solid { background: var(--tone, var(--idle)); }
        .badge.outline { color: var(--tone, var(--muted)); border-color: var(--tone, var(--line)); }
        .toolbar { position: sticky; top: 0; z-index: 3; display: grid; gap: 8px;
          background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 10px; margin: 0 0 12px; }
        .toolbar .row { display: grid; gap: 8px; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); }
        .field { display: grid; gap: 3px; font-size: 0.8rem; color: var(--muted); }
        .field select, .field input { width: 100%; padding: 6px 7px; border: 1px solid var(--line); border-radius: 7px;
          background: var(--bg); color: var(--ink); font: inherit; font-size: 0.85rem; }
        .buttons { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
        .buttons button { padding: 6px 11px; border: 1px solid var(--line); border-radius: 7px; background: var(--bg);
          color: var(--ink); font: inherit; font-size: 0.82rem; cursor: pointer; }
        .buttons button:hover, .buttons button:focus-visible { border-color: var(--accent); }
        #f-count { color: var(--muted); font-size: 0.82rem; margin-left: auto; }
        #post-list { display: grid; gap: 12px; }
        /* Переход по якорю не должен прятать заголовок под закреплённой панелью:
           --toolbar-offset выставляет встроенный сценарий по фактической высоте панели,
           а на узких экранах панель не закреплена и переменная равна нулю. */
        article.post, section[id] { scroll-margin-top: calc(var(--toolbar-offset, 0px) + 8px); }
        article.post { background: var(--panel); border: 1px solid var(--line); border-left: 5px solid var(--tone, var(--idle));
          border-radius: 10px; padding: 11px; min-width: 0; }
        article.post[data-stage="profile"] { --tone: var(--pos); }
        article.post[data-stage="not_profile"] { --tone: var(--neg); }
        article.post[data-stage="uncertain"] { --tone: var(--warn); }
        article.post[data-stage="pending"], article.post[data-stage="running"] { --tone: var(--wait); }
        article.post[data-stage="failed"] { --tone: var(--bad); }
        article.post[data-stage="no_job"], article.post[data-stage="not_admitted"],
        article.post[data-stage="stale_only"], article.post[data-stage="succeeded_without_decision"] { --tone: var(--idle); }
        .post-head { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: 0 0 8px; min-width: 0; }
        .post-num { font-weight: 700; font-variant-numeric: tabular-nums; }
        .post-link { font-size: 0.8rem; }
        .when { color: var(--muted); font-size: 0.78rem; margin-left: auto; }
        .text { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; font: inherit; font-size: 0.9rem; }
        .author-text { background: rgba(91, 100, 114, 0.07); border-radius: 7px; padding: 8px 10px; margin: 0 0 8px; }
        .prepared { border: 1px dashed var(--line); border-radius: 7px; padding: 8px 10px; margin: 0 0 8px; }
        .prepared.limited { border-style: solid; border-color: var(--warn); }
        .label { display: block; color: var(--muted); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.03em; margin: 0 0 3px; }
        blockquote { margin: 0 0 6px; padding: 6px 10px; border-left: 3px solid var(--line); background: rgba(91, 100, 114, 0.07);
          border-radius: 4px; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 0.86rem; }
        .evidence { border: 1px solid var(--line); border-radius: 7px; padding: 7px 10px; margin: 0 0 6px;
          white-space: pre-wrap; overflow-wrap: anywhere; font-size: 0.88rem; }
        .evidence.exact { border-color: var(--pos); }
        .evidence.unverified { border-color: var(--warn); }
        mark { background: var(--mark); color: inherit; padding: 0 1px; }
        details { margin: 6px 0 0; }
        details > summary { cursor: pointer; font-size: 0.85rem; color: var(--accent); }
        details[open] > summary { margin: 0 0 8px; }
        dl.facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 6px 14px; margin: 0 0 4px; }
        dl.facts div { border-bottom: 1px dotted var(--line); padding: 0 0 4px; }
        dl.facts dt { color: var(--muted); font-size: 0.75rem; }
        dl.facts dd { margin: 0; font-size: 0.86rem; overflow-wrap: anywhere; }
        .empty { color: var(--muted); font-size: 0.86rem; }
        footer { color: var(--muted); font-size: 0.78rem; padding: 8px 2px 24px; }
        @media (max-width: 520px) {
          .wrap { padding: 10px; }
          h1 { font-size: 1.2rem; }
          .when { margin-left: 0; }
          article.post { padding: 9px; }
        }
        @media (max-width: 640px) {
          .toolbar { position: static; gap: 6px; padding: 8px; }
          .toolbar .row { gap: 6px; grid-template-columns: repeat(auto-fit, minmax(132px, 1fr)); }
          .field select, .field input { padding: 5px 6px; font-size: 0.8rem; }
          .buttons { gap: 6px; }
          .buttons button { padding: 5px 9px; font-size: 0.78rem; }
        }
        @media (max-height: 480px) {
          .toolbar { position: static; }
        }
        @media (min-width: 1400px) {
          #post-list { grid-template-columns: 1fr 1fr; align-items: start; }
        }
        @media (min-width: 1800px) {
          .wrap { max-width: 1760px; }
        }
        [hidden] { display: none !important; }
        """;

    private const string ClientScript = """
        (function () {
          'use strict';
          var list = document.getElementById('post-list');
          if (!list) { return; }
          var articles = Array.prototype.slice.call(list.querySelectorAll('article.post'));
          var controls = {
            stage: document.getElementById('f-stage'),
            reason: document.getElementById('f-reason'),
            addressing: document.getElementById('f-addressing'),
            job: document.getElementById('f-job'),
            excluded: document.getElementById('f-excluded'),
            search: document.getElementById('f-search'),
            sort: document.getElementById('f-sort'),
            count: document.getElementById('f-count')
          };
          function value(name) {
            var control = controls[name];
            return control && typeof control.value === 'string' ? control.value : '';
          }
          function query() {
            return value('search').trim().toLowerCase();
          }
          function matches(article) {
            var stage = value('stage');
            if (stage && article.dataset.stage !== stage) { return false; }
            var reason = value('reason');
            if (reason && article.dataset.reason !== reason) { return false; }
            var addressing = value('addressing');
            if (addressing && article.dataset.addressing !== addressing) { return false; }
            var job = value('job');
            if (job && article.dataset.job !== job) { return false; }
            var excluded = value('excluded');
            if (excluded === 'only' && article.dataset.excluded !== 'true') { return false; }
            if (excluded === 'hide' && article.dataset.excluded === 'true') { return false; }
            var text = query();
            if (!text) { return true; }
            var haystack = (article.dataset.search || '') + ' ' + (article.dataset.number || '') + ' ' +
              (article.dataset.stage || '') + ' ' + (article.dataset.reason || '') + ' ' + (article.dataset.addressing || '');
            return haystack.toLowerCase().indexOf(text) >= 0;
          }
          function apply() {
            var shown = 0;
            for (var index = 0; index < articles.length; index++) {
              var visible = matches(articles[index]);
              articles[index].hidden = !visible;
              if (visible) { shown++; }
            }
            if (controls.count) { controls.count.textContent = 'показано ' + shown + ' из ' + articles.length; }
          }
          function number(article) {
            var parsed = parseInt(article.dataset.number, 10);
            return isNaN(parsed) ? 0 : parsed;
          }
          function compare(left, right) {
            var mode = value('sort') || 'number-asc';
            if (mode === 'number-desc') { return number(right) - number(left); }
            if (mode === 'stage') {
              var byStage = (left.dataset.stage || '').localeCompare(right.dataset.stage || '', 'ru');
              return byStage !== 0 ? byStage : number(left) - number(right);
            }
            if (mode === 'reason') {
              var byReason = (left.dataset.reason || '').localeCompare(right.dataset.reason || '', 'ru');
              return byReason !== 0 ? byReason : number(left) - number(right);
            }
            return number(left) - number(right);
          }
          function sortArticles() {
            var sorted = articles.slice().sort(compare);
            for (var index = 0; index < sorted.length; index++) { list.appendChild(sorted[index]); }
            articles = sorted;
          }
          function toggleDetails(open) {
            for (var index = 0; index < articles.length; index++) {
              var items = articles[index].querySelectorAll('details');
              for (var inner = 0; inner < items.length; inner++) { items[inner].open = open; }
            }
          }
          var reactive = ['stage', 'reason', 'addressing', 'job', 'excluded', 'search'];
          for (var index = 0; index < reactive.length; index++) {
            var control = controls[reactive[index]];
            if (!control) { continue; }
            control.addEventListener('input', apply);
            control.addEventListener('change', apply);
          }
          if (controls.sort) { controls.sort.addEventListener('change', function () { sortArticles(); apply(); }); }
          var reset = document.getElementById('b-reset');
          if (reset) {
            reset.addEventListener('click', function () {
              for (var index = 0; index < reactive.length; index++) {
                if (controls[reactive[index]]) { controls[reactive[index]].value = ''; }
              }
              if (controls.sort) { controls.sort.value = 'number-asc'; }
              sortArticles();
              apply();
            });
          }
          var expand = document.getElementById('b-expand');
          if (expand) { expand.addEventListener('click', function () { toggleDetails(true); }); }
          var collapse = document.getElementById('b-collapse');
          if (collapse) { collapse.addEventListener('click', function () { toggleDetails(false); }); }
          // Фактическая высота закреплённой панели: по ней считается отступ перехода по якорю.
          // Если панель не закреплена (узкий экран), переменная обнуляется, чтобы не было пустоты.
          function syncToolbarOffset() {
            var toolbar = document.querySelector('.toolbar');
            var offset = 0;
            if (toolbar && window.getComputedStyle(toolbar).position === 'sticky') {
              offset = Math.round(toolbar.getBoundingClientRect().height);
            }
            document.documentElement.style.setProperty('--toolbar-offset', offset + 'px');
          }
          window.addEventListener('resize', syncToolbarOffset);
          window.addEventListener('orientationchange', syncToolbarOffset);
          syncToolbarOffset();
          apply();
        }());
        """;

    /// <summary>Строит автономный HTML-документ отчёта.</summary>
    /// <param name="document">Документ отчёта.</param>
    /// <returns>Полный HTML-документ одним текстом.</returns>
    /// <exception cref="ArgumentNullException">Документ не передан.</exception>
    public static string Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var html = new StringBuilder(64 * 1024);
        html.Append("<!DOCTYPE html>\n<html lang=\"ru\">\n<head>\n<meta charset=\"utf-8\">\n");
        // Один и тот же нормализованный текст идёт и в хеш политики, и в документ:
        // HTML-парсер приводит CRLF к LF до проверки встроенного блока.
        var styleSheet = NormalizeNewlines(StyleSheet);
        var clientScript = NormalizeNewlines(ClientScript);
        var policy = ContentSecurityPolicy
            .Replace("{0}", Hash(clientScript, "sha256"), StringComparison.Ordinal)
            .Replace("{1}", Hash(styleSheet, "sha256"), StringComparison.Ordinal);
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(policy).Append("\">\n");
        html.Append("<meta name=\"referrer\" content=\"no-referrer\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"generator\" content=\"").Append(Escape("AnketaParser " + document.ToolVersion)).Append("\">\n");
        html.Append("<title>").Append(Escape("Отбор анкет: снимок " + document.Snapshot.SourceKey + " — отчёт")).Append("</title>\n");
        // Текст встроенных блоков совпадает с тем, что покрывает хеш политики: без лишних переводов строки.
        html.Append("<style>").Append(styleSheet).Append("</style>\n</head>\n<body>\n<div class=\"wrap\">\n");

        AppendHeader(html, document);
        AppendJump(html, document);
        html.Append("<main>\n");
        AppendSnapshot(html, document);
        AppendSummary(html, document);
        AppendPosts(html, document);
        AppendOutside(html, document);
        html.Append("</main>\n");

        html.Append("<footer>\n<p>Отчёт построен только по данным хранилища продукта: база не изменялась, " +
            "модель не вызывалась, сеть не использовалась. Файл автономен: стили и сценарий встроены, " +
            "внешние ресурсы отсутствуют.</p>\n<p>Адресация поста — метаданные ответа (op, op_reply, none, other_reply); " +
            "причиной отсева она не является. Отсутствие решения модели не означает «не анкета».</p>\n</footer>\n</div>\n");
        html.Append("<script>").Append(clientScript).Append("</script>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendHeader(StringBuilder html, ReportDocument document)
    {
        var snapshot = document.Snapshot;
        html.Append("<header>\n<h1>Отбор анкет: три этапа</h1>\n");
        html.Append("<p class=\"sub\">Снимок источника <strong>").Append(Escape(snapshot.SourceKey)).Append("</strong>");
        if (snapshot.StartedAt is not null && snapshot.FinishedAt is not null)
        {
            html.Append(" · окно скана ").Append(Escape(MomentOr(snapshot.StartedAt)))
                .Append(" — ").Append(Escape(MomentOr(snapshot.FinishedAt)));
        }

        html.Append(" · настройка ").Append(Escape(snapshot.Status + "/" + snapshot.Phase));
        html.Append(" · постов в снимке: ").Append(Number(document.Posts.Count));
        html.Append("</p>\n<p class=\"sub\">Отчёт сформирован ").Append(Escape(Moment(document.GeneratedAt)))
            .Append(" инструментом AnketaParser ").Append(Escape(document.ToolVersion));
        if (document.RunMetadata.Loaded)
        {
            html.Append(" · метаданные запуска: ");
            if (document.RunMetadata.Path is { } metadataPath)
            {
                // В шапке — только имя файла: полный путь остаётся подсказкой, а сами факты
                // приведены в разделе снимка, поэтому метаданные не теряются.
                html.Append("<span title=\"").Append(Escape(metadataPath)).Append("\">")
                    .Append(Escape(FileName(metadataPath))).Append("</span>");
            }
            else
            {
                html.Append("файл передан");
            }
        }
        else
        {
            html.Append(" · метаданные запуска не переданы: движок и параметры запуска в отчёте ограничены сохранёнными решениями");
        }

        html.Append("</p>\n</header>\n");
    }

    private static void AppendJump(StringBuilder html, ReportDocument document)
    {
        html.Append("<nav class=\"jump\" aria-label=\"Быстрый переход\">\n");
        html.Append("<a href=\"#snapshot\">Снимок и ограничения</a>\n");
        for (var index = 0; index < document.Summary.Count; index++)
        {
            html.Append("<a href=\"#g").Append(Number(index + 1)).Append("\">")
                .Append(Escape(document.Summary[index].Title)).Append("</a>\n");
        }

        html.Append("<a href=\"#posts\">Детализация постов</a>\n");
        if (document.PostsOutsideWindow.Count > 0)
        {
            html.Append("<a href=\"#outside\">Посты вне снимка</a>\n");
        }

        html.Append("</nav>\n");
    }

    private static void AppendSnapshot(StringBuilder html, ReportDocument document)
    {
        var snapshot = document.Snapshot;
        html.Append("<section id=\"snapshot\" class=\"card\">\n<h2>Снимок: источник, границы и метаданные запуска</h2>\n");
        html.Append("<div class=\"scroll\">\n<table class=\"data\">\n<caption>Факты одного зафиксированного скана (SourceScan) и источника.</caption>\n<thead><tr>")
            .Append("<th scope=\"col\">Показатель</th><th scope=\"col\">Значение</th><th scope=\"col\">Пояснение</th></tr></thead>\n<tbody>\n");
        Row(html, "Источник (ключ)", snapshot.SourceKey, "Source.Key");
        Row(html, "Адрес источника", snapshot.SourceUrl, "Source.Url; ссылка на ресурс не загружается отчётом");
        Row(html, "Статус скана", snapshot.Status, "SourceScan.Status");
        Row(html, "Фаза скана", snapshot.Phase, "SourceScan.Phase");
        Row(html, "Отказ скана", snapshot.Failure, "SourceScan.Failure");
        Row(html, "Начало окна", MomentOr(snapshot.StartedAt), "SourceScan.StartedAt");
        Row(html, "Конец окна", MomentOr(snapshot.FinishedAt), "SourceScan.FinishedAt; посты снимка — LastSeenAt внутри окна");
        Row(html, "Последний успешный скан", MomentOr(snapshot.LastSuccessAt), "SourceScan.LastSuccessAt");
        Row(html, "Получено записей", Number(snapshot.Received), "SourceScan.Metrics.Received");
        Row(html, "Применено записей", Number(snapshot.Applied), "SourceScan.Applied");
        Row(html, "Постов в окне скана", Number(snapshot.IdsInWindow), "множество OriginalPost.ExternalId по границам окна");
        Row(html, "Постов вне окна", Number(snapshot.PostsOutsideWindow), "увидены другим сканом; в снимок не входят");
        Row(html, "Тред закрыт", snapshot.Closed ? "да" : "нет", "SourceScan.Metrics.Closed");
        html.Append("</tbody>\n</table>\n</div>\n");

        if (snapshot.Metrics is { } metrics)
        {
            html.Append("<h3>Метрики скана</h3>\n<div class=\"scroll\">\n<table class=\"data\">\n<caption>Счётчики скана описывают адресацию и решения фильтра в целом: " +
                "other_reply здесь не отделён от candidates, поэтому причины отсева в отчёте считаются по постам снимка.</caption>\n")
                .Append("<thead><tr><th scope=\"col\">Показатель</th><th scope=\"col\" class=\"num\">Значение</th></tr></thead>\n<tbody>\n");
            Metric(html, "получено постов", metrics.Received);
            Metric(html, "новых постов", metrics.NewPosts);
            Metric(html, "изменённых постов", metrics.ChangedPosts);
            Metric(html, "повторных постов", metrics.RepeatedPosts);
            Metric(html, "решений candidate", metrics.Candidates);
            Metric(html, "двусмысленных контактов", metrics.Ambiguous);
            Metric(html, "распознанных контактов", metrics.Contacts);
            Metric(html, "открывающих постов", metrics.OpeningPosts);
            Metric(html, "ответов другим постам (адресация)", metrics.OtherReplies);
            Metric(html, "без своего контакта", metrics.NoOwnContact);
            Metric(html, "исключено модерацией", metrics.Excluded);
            html.Append("</tbody>\n</table>\n</div>\n");
        }

        html.Append("<h3>Метаданные движка и параметров запуска</h3>\n");
        if (document.RunMetadata.All.Count == 0)
        {
            html.Append("<p class=\"empty\">Метаданные запуска не переданы либо не содержат известных полей: " +
                "сведения о модели в детализации ограничены сохранёнными решениями модели.</p>\n");
        }
        else
        {
            if (document.RunMetadata.Facts.Count == 0)
            {
                html.Append("<p class=\"empty\">Распознаваемых полей не найдено: ниже показаны все значения переданного файла.</p>\n");
            }
            else
            {
                html.Append("<div class=\"scroll\">\n<table class=\"data\">\n<caption>Источник — переданный файл метаданных запуска; " +
                    "это описание стенда, а не исторический факт конкретного решения.</caption>\n")
                    .Append("<thead><tr><th scope=\"col\">Показатель</th><th scope=\"col\">Значение</th></tr></thead>\n<tbody>\n");
                foreach (var item in document.RunMetadata.Facts)
                {
                    Row(html, item.Name, item.Value, null);
                }

                html.Append("</tbody>\n</table>\n</div>\n");
            }

            // Исходные значения показываются независимо от сводки: одно поле может дать
            // несколько фактов, а файл без известных имён всё равно должен быть виден.
            html.Append("<details><summary>Все поля файла метаданных (")
                .Append(Number(document.RunMetadata.All.Count)).Append(")</summary>\n<div class=\"scroll\">\n")
                .Append("<table class=\"data\"><thead><tr><th scope=\"col\">Поле</th><th scope=\"col\">Значение</th></tr></thead>\n<tbody>\n");
            foreach (var item in document.RunMetadata.All)
            {
                Row(html, item.Name, item.Value, null);
            }

            html.Append("</tbody>\n</table>\n</div>\n</details>\n");
        }

        html.Append("<h3>Ограничения данных и оговорки</h3>\n<ul class=\"notices\">\n");
        foreach (var notice in document.Notices)
        {
            html.Append("<li class=\"").Append(Escape(notice.Level)).Append("\">").Append(Escape(notice.Text)).Append("</li>\n");
        }

        html.Append("</ul>\n</section>\n");
    }

    private static void AppendSummary(StringBuilder html, ReportDocument document)
    {
        // Доли округляются до 0,1 %: сумма показанных долей одного разбиения может не равняться
        // 100 % — это следствие округления, а не расхождение данных.
        html.Append("<p class=\"note\" id=\"share-rounding\">Доли округлены до 0,1 %: сумма показанных долей " +
            "может отличаться от 100 %, значения при этом не изменяются.</p>\n");
        for (var index = 0; index < document.Summary.Count; index++)
        {
            var group = document.Summary[index];
            html.Append("<section id=\"g").Append(Number(index + 1)).Append("\" class=\"card\">\n<h2>")
                .Append(Escape(group.Title)).Append("</h2>\n");
            if (!string.IsNullOrWhiteSpace(group.Caption))
            {
                html.Append("<p class=\"note\">").Append(Escape(group.Caption)).Append("</p>\n");
            }

            html.Append("<div class=\"scroll\">\n<table class=\"data\">\n<thead><tr>")
                .Append("<th scope=\"col\">Показатель</th><th scope=\"col\" class=\"num\">Значение</th>")
                .Append("<th scope=\"col\" class=\"num\">Знаменатель</th><th scope=\"col\" class=\"num\">Доля</th>")
                .Append("<th scope=\"col\">Примечание</th></tr></thead>\n<tbody>\n");
            foreach (var counter in group.Counters)
            {
                html.Append("<tr><th scope=\"row\">").Append(Escape(counter.Label)).Append("</th><td class=\"num\">")
                    .Append(Number(counter.Value)).Append("</td><td class=\"num\">")
                    .Append(counter.Total is { } total ? Number(total) : "—").Append("</td><td class=\"num\">")
                    .Append(Escape(Share(counter))).Append("</td><td class=\"wrap-cell\">")
                    .Append(Escape(counter.Note ?? string.Empty)).Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n</div>\n</section>\n");
        }
    }

    private static void AppendPosts(StringBuilder html, ReportDocument document)
    {
        html.Append("<section id=\"posts\">\n<h2>Детализация постов снимка</h2>\n");
        html.Append("<p class=\"note\">Показаны все посты снимка, включая отсеянные первичным фильтром и исключённые модерацией. " +
            "Фрагмент-основание подсвечивается только при точном воспроизведении текста; в остальных случаях " +
            "фрагмент выводится без подсветки с указанием причины. " +
            "Фильтры, поиск и сортировка работают локально в браузере без обращения к сети.</p>\n");
        AppendToolbar(html, document);
        if (document.Posts.Count == 0)
        {
            html.Append("<p class=\"empty\">Снимок не содержит постов.</p>\n");
            html.Append("</section>\n");
            return;
        }

        html.Append("<div id=\"post-list\" aria-live=\"polite\">\n");
        foreach (var post in document.Posts)
        {
            AppendPost(html, post);
        }

        html.Append("</div>\n</section>\n");
    }

    private static void AppendToolbar(StringBuilder html, ReportDocument document)
    {
        html.Append("<div class=\"toolbar\" role=\"region\" aria-label=\"Фильтры и поиск\">\n<div class=\"row\">\n");
        Select(html, "f-stage", "Этап решения модели", "все этапы", Distinct(document.Posts.Select(post => (post.Stage, post.StageLabel))));
        Select(html, "f-reason", "Решение первичного фильтра", "все решения",
            Distinct(document.Posts.Select(post => (post.Filter.Reason, post.Filter.ReasonLabel))));
        Select(html, "f-addressing", "Адресация поста", "любая адресация",
            Distinct(document.Posts.Select(post => (post.Filter.Addressing, post.Filter.AddressingLabel))));
        Select(html, "f-job", "Состояние задания модели", "любое состояние",
            Distinct(document.Posts.Select(post => (JobState(post), JobStateLabel(post)))));
        html.Append("<label class=\"field\">Исключение модерацией<select id=\"f-excluded\">")
            .Append("<option value=\"\">все посты</option><option value=\"only\">только исключённые</option>")
            .Append("<option value=\"hide\">без исключённых</option></select></label>\n");
        html.Append("<label class=\"field\">Сортировка<select id=\"f-sort\">")
            .Append("<option value=\"number-asc\">номер по возрастанию</option><option value=\"number-desc\">номер по убыванию</option>")
            .Append("<option value=\"stage\">этап решения модели</option><option value=\"reason\">решение фильтра</option>")
            .Append("</select></label>\n");
        html.Append("<label class=\"field\">Поиск по номеру и тексту<input id=\"f-search\" type=\"search\" ")
            .Append("inputmode=\"search\" autocomplete=\"off\" placeholder=\"например, номер поста или слово из текста\"></label>\n");
        html.Append("</div>\n<div class=\"buttons\">\n<button type=\"button\" id=\"b-expand\">Раскрыть подробности</button>\n")
            .Append("<button type=\"button\" id=\"b-collapse\">Свернуть подробности</button>\n")
            .Append("<button type=\"button\" id=\"b-reset\">Сбросить фильтры</button>\n")
            .Append("<span id=\"f-count\"></span>\n</div>\n</div>\n");
    }

    private static void AppendPost(StringBuilder html, ReportPostView post)
    {
        html.Append("<article class=\"post\" id=\"post-").Append(Escape(post.Number)).Append("\" data-number=\"")
            .Append(Escape(post.Number)).Append("\" data-stage=\"").Append(Escape(post.Stage))
            .Append("\" data-reason=\"").Append(Escape(post.Filter.Reason))
            .Append("\" data-addressing=\"").Append(Escape(post.Filter.Addressing))
            .Append("\" data-job=\"").Append(Escape(JobState(post)))
            .Append("\" data-excluded=\"").Append(post.ModerationExcluded ? "true" : "false")
            .Append("\" data-search=\"").Append(Escape(post.SearchText)).Append("\">\n");
        html.Append("<div class=\"post-head\">\n<span class=\"post-num\">№ ").Append(Escape(post.Number)).Append("</span>\n");
        if (post.OriginLink is { } link)
        {
            html.Append("<a class=\"post-link\" href=\"").Append(Escape(link))
                .Append("\" rel=\"noreferrer noopener\" target=\"_blank\">оригинал</a>\n");
        }
        else
        {
            html.Append("<span class=\"badge\">ссылка на оригинал недоступна</span>\n");
        }

        html.Append("<span class=\"badge solid tone-").Append(Escape(Tone(post.Stage))).Append("\">")
            .Append(Escape(StageBadge(post))).Append("</span>\n");
        html.Append("<span class=\"badge outline tone-idle\">решение фильтра: ").Append(Escape(post.Filter.ReasonLabel)).Append("</span>\n");
        html.Append("<span class=\"badge outline tone-idle\">адресация: ").Append(Escape(post.Filter.AddressingLabel)).Append("</span>\n");
        if (post.ModerationExcluded)
        {
            html.Append("<span class=\"badge solid tone-bad\">исключён модерацией</span>\n");
        }

        html.Append("<span class=\"when\">опубликован: ").Append(Escape(post.PublishedAt ?? "неизвестно"))
            .Append(" · виден: ").Append(Escape(post.LastSeenAt)).Append("</span>\n</div>\n");

        html.Append("<div class=\"author-text\"><span class=\"label\">Авторский текст (как сохранён)</span><pre class=\"text\">")
            .Append(Escape(post.Text.AuthorText ?? "текст ревизии недоступен")).Append("</pre></div>\n");
        html.Append("<p class=\"note\">").Append(Escape(post.Text.TextNote)).Append("</p>\n");

        html.Append("<div class=\"prepared").Append(post.Text.PreparedStatus == "reconstructed" ? string.Empty : " limited")
            .Append("\"><span class=\"label\">Подготовленный текст")
            .Append(post.Text.PreparedStatus == "reconstructed" ? " (реконструкция)" : " (не воспроизводится)")
            .Append("</span>");
        if (post.Text.PreparedText is { } prepared)
        {
            html.Append("<pre class=\"text\">").Append(Escape(prepared)).Append("</pre>");
        }
        else
        {
            html.Append("<p class=\"text\">Точное воспроизведение невозможно: маскированный текст не показан.</p>");
        }

        html.Append("<p class=\"note\">").Append(Escape(post.Text.PreparedNote)).Append("</p></div>\n");

        html.Append("<details>\n<summary>Подробности: фильтр, evidence, анкета, ревизии, задания</summary>\n");
        AppendFilter(html, post);
        AppendDossier(html, post);
        AppendText(html, post);
        AppendLlm(html, post);
        AppendJobs(html, post);
        html.Append("</details>\n</article>\n");
    }

    private static void AppendFilter(StringBuilder html, ReportPostView post)
    {
        html.Append("<h3>Первичный фильтр, адресация и контакты</h3>\n<dl class=\"facts\">\n");
        Fact(html, "Решение фильтра", post.Filter.ReasonLabel);
        Fact(html, "Код решения", post.Filter.Reason);
        Fact(html, "Пояснение решения", post.Filter.ReasonNote);
        Fact(html, "Адресация", post.Filter.AddressingLabel);
        Fact(html, "Код адресации", post.Filter.Addressing);
        Fact(html, "Оговорка адресации", post.Filter.AddressingNote);
        Fact(html, "Исключён модерацией", post.ModerationExcluded ? "да" : "нет");
        Fact(html, "Допущен к анализу модели", post.Filter.AdmittedToLlm ? "да" : "нет");
        Fact(html, "Условие допуска", post.Filter.AdmittedNote);
        Fact(html, "Контактов распознано", Number(post.Filter.ContactsCount));
        Fact(html, "Разные значения одной платформы", post.Filter.Ambiguous ? "да" : "нет");
        html.Append("</dl>\n");
    }

    private static void AppendDossier(StringBuilder html, ReportPostView post)
    {
        html.Append("<h3>Фрагмент-основание решения модели</h3>\n");
        var evidence = post.Llm.Decision;
        var tone = evidence.Status == ReportEvidenceView.Exact ? " exact"
            : evidence.Fragment is null ? string.Empty : " unverified";
        html.Append("<div class=\"evidence").Append(tone).Append("\">");
        if (evidence.Fragment is { } fragment)
        {
            html.Append(evidence.Status == ReportEvidenceView.Exact
                ? "<mark>" + Escape(fragment) + "</mark>"
                : Escape(fragment));
        }
        else
        {
            html.Append("<span class=\"note\">фрагмент не показан</span>");
        }

        html.Append("</div>\n<p class=\"note\">").Append(Escape(evidence.Note));
        if (evidence.Span is { } span)
        {
            html.Append(evidence.Note.EndsWith('.') ? " " : ". ")
                .Append("Координаты UTF-16: ").Append(Offset(span.Start)).Append('+').Append(Offset(span.Length)).Append('.');
        }

        // Подсветка показывается только при точном воспроизведении. Явная оговорка нужна там,
        // где сохранённое пояснение о ней ещё не говорит, иначе формулировка повторялась бы дважды.
        if (evidence.Status != ReportEvidenceView.Exact &&
            !evidence.Note.Contains("подсветка", StringComparison.OrdinalIgnoreCase))
        {
            html.Append(" Подсветка не выполняется: точное воспроизведение не подтверждено.");
        }

        html.Append("</p>\n");

        html.Append("<h3>Поля анкеты</h3>\n");
        if (post.Llm.Fields.Count == 0)
        {
            html.Append("<p class=\"empty\">Подтверждённых полей нет.</p>\n");
        }
        else
        {
            html.Append("<div class=\"scroll\">\n<table class=\"data\"><thead><tr><th scope=\"col\">Поле</th>")
                .Append("<th scope=\"col\">Значение</th><th scope=\"col\">Подтверждение</th></tr></thead>\n<tbody>\n");
            foreach (var field in post.Llm.Fields)
            {
                html.Append("<tr><th scope=\"row\">").Append(Escape(field.Label)).Append("</th><td class=\"wrap-cell\">")
                    .Append(Escape(field.Value ?? "—")).Append("</td><td class=\"wrap-cell\">")
                    .Append(Escape(field.Evidence.Note));
                if (field.Evidence.Fragment is { } fieldFragment)
                {
                    html.Append(": «").Append(Escape(fieldFragment)).Append('»');
                }

                html.Append("</td></tr>\n");
            }

            html.Append("</tbody></table>\n</div>\n");
        }

        if (post.Llm.UnsupportedFields.Count > 0)
        {
            html.Append("<h3>Неподтверждённые поля</h3>\n<ul class=\"notices\">\n");
            foreach (var unsupported in post.Llm.UnsupportedFields)
            {
                html.Append("<li class=\"limitation\">").Append(Escape(unsupported)).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }
    }

    private static void AppendText(StringBuilder html, ReportPostView post)
    {
        if (post.Text.Quotes.Count > 0)
        {
            html.Append("<h3>Цитаты треда</h3>\n");
            foreach (var quote in post.Text.Quotes)
            {
                html.Append("<blockquote>").Append(Escape(quote)).Append("</blockquote>\n");
            }
        }

        if (post.Text.Replies.Count > 0)
        {
            html.Append("<h3>Ответы на посты треда</h3>\n<ul class=\"notices\">\n");
            foreach (var reply in post.Text.Replies)
            {
                html.Append("<li>").Append(Escape(reply.Label)).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        html.Append("<h3>Наблюдения контактов актуальной ревизии</h3>\n");
        if (post.Text.Contacts.Count == 0)
        {
            html.Append("<p class=\"empty\">Наблюдений контактов нет.</p>\n");
        }
        else
        {
            html.Append("<div class=\"scroll\">\n<table class=\"data\"><thead><tr><th scope=\"col\">Платформа</th>")
                .Append("<th scope=\"col\">Как записан</th><th scope=\"col\">Нормализовано</th><th scope=\"col\" class=\"num\">Уверенность</th>")
                .Append("<th scope=\"col\">Происхождение</th><th scope=\"col\">Проверка</th></tr></thead>\n<tbody>\n");
            foreach (var contact in post.Text.Contacts)
            {
                html.Append("<tr><td>").Append(Escape(contact.Platform)).Append("</td><td class=\"wrap-cell\">")
                    .Append(Escape(contact.RawValue)).Append("</td><td class=\"wrap-cell\">")
                    .Append(Escape(contact.NormalizedValue)).Append("</td><td class=\"num\">")
                    .Append(Escape(contact.Confidence)).Append("</td><td>").Append(Escape(contact.Provenance))
                    .Append("</td><td class=\"wrap-cell\">").Append(Escape(contact.TextNote ?? string.Empty))
                    .Append("</td></tr>\n");
            }

            html.Append("</tbody></table>\n</div>\n");
        }
    }

    private static void AppendLlm(StringBuilder html, ReportPostView post)
    {
        var llm = post.Llm;
        html.Append("<h3>Решение модели по актуальной ревизии</h3>\n<dl class=\"facts\">\n");
        Fact(html, "Этап", llm.StatusLabel);
        Fact(html, "Код этапа", llm.Status);
        Fact(html, "Оговорка", llm.Note);
        Fact(html, "Класс решения", llm.ClassLabel);
        Fact(html, "Код класса", llm.Class);
        Fact(html, "Основание", llm.ReasonLabel);
        Fact(html, "Код основания", llm.Reason);
        Fact(html, "Модель", llm.Model);
        Fact(html, "Обработчик", llm.Engine);
        Fact(html, "Версия конвейера", llm.PipelineVersion);
        Fact(html, "Версия схемы", llm.SchemaVersion);
        Fact(html, "Состояние анкеты", llm.ProfileStateLabel);
        Fact(html, "Предпочтения", llm.Preferences);
        Fact(html, "Описание", llm.Description);
        html.Append("</dl>\n");

        html.Append("<h3>История решений модели</h3>\n");
        if (llm.Decisions.Count == 0)
        {
            html.Append("<p class=\"empty\">Решений модели нет.</p>\n");
        }
        else
        {
            html.Append("<div class=\"scroll\">\n<table class=\"data\"><thead><tr><th scope=\"col\">Ревизия</th>")
                .Append("<th scope=\"col\">Актуальна</th><th scope=\"col\">Создано</th><th scope=\"col\">Класс</th>")
                .Append("<th scope=\"col\">Основание</th><th scope=\"col\">Модель</th><th scope=\"col\">Конвейер</th>")
                .Append("<th scope=\"col\">Анкета</th></tr></thead>\n<tbody>\n");
            foreach (var decision in llm.Decisions)
            {
                html.Append("<tr><td class=\"wrap-cell\">").Append(Escape(decision.RevisionId))
                    .Append("</td><td>").Append(decision.Current ? "да" : "устаревшая")
                    .Append("</td><td>").Append(Escape(decision.CreatedAt))
                    .Append("</td><td>").Append(Escape(decision.ClassLabel ?? "—"))
                    .Append("</td><td>").Append(Escape(decision.ReasonLabel ?? "—"))
                    .Append("</td><td>").Append(Escape(decision.Model ?? "—"))
                    .Append("</td><td>").Append(Escape(decision.PipelineVersion ?? "—"))
                    .Append("</td><td>").Append(Escape(decision.ProfileState ?? "—")).Append("</td></tr>\n");
            }

            html.Append("</tbody></table>\n</div>\n");
        }

        html.Append("<h3>Ревизии поста</h3>\n<div class=\"scroll\">\n<table class=\"data\"><thead><tr>")
            .Append("<th scope=\"col\">Ревизия</th><th scope=\"col\">Актуальна</th><th scope=\"col\">Получена</th>")
            .Append("<th scope=\"col\">Хеш</th><th scope=\"col\">Решение фильтра</th><th scope=\"col\">Адресация</th>")
            .Append("<th scope=\"col\" class=\"num\">Контактов</th><th scope=\"col\">Двусмысленность</th>")
            .Append("<th scope=\"col\" class=\"num\">Решений</th><th scope=\"col\" class=\"num\">Заданий</th>")
            .Append("<th scope=\"col\">Текст</th></tr></thead>\n<tbody>\n");
        foreach (var revision in post.Revisions)
        {
            html.Append("<tr><td class=\"wrap-cell\">").Append(Escape(revision.Id))
                .Append("</td><td>").Append(revision.Current ? "да" : "устаревшая")
                .Append("</td><td>").Append(Escape(revision.ReceivedAt))
                .Append("</td><td class=\"wrap-cell\">").Append(Escape(revision.Hash))
                .Append("</td><td>").Append(Escape(revision.Reason ?? "—"))
                .Append("</td><td>").Append(Escape(revision.Addressing ?? "—"))
                .Append("</td><td class=\"num\">").Append(Number(revision.Contacts))
                .Append("</td><td>").Append(revision.Ambiguous ? "да" : "нет")
                .Append("</td><td class=\"num\">").Append(Number(revision.Analyses))
                .Append("</td><td class=\"num\">").Append(Number(revision.Jobs))
                .Append("</td><td>").Append(revision.HasText ? "есть" : "нет").Append("</td></tr>\n");
        }

        html.Append("</tbody></table>\n</div>\n");
    }

    private static void AppendJobs(StringBuilder html, ReportPostView post)
    {
        html.Append("<h3>Задания и попытки</h3>\n");
        if (post.Llm.Jobs.Count == 0)
        {
            html.Append("<p class=\"empty\">Заданий по ревизиям поста нет.</p>\n");
            return;
        }

        foreach (var job in post.Llm.Jobs)
        {
            html.Append("<h4>").Append(Escape(job.KindLabel)).Append(": ").Append(Escape(job.StateLabel)).Append("</h4>\n")
                .Append("<dl class=\"facts\">\n");
            Fact(html, "Идентификатор", job.Id.ToString("D", CultureInfo.InvariantCulture));
            Fact(html, "Состояние", job.State);
            Fact(html, "Версия конвейера", job.LlmVersion);
            Fact(html, "Попыток", Number(job.AttemptCount) + " из " + Number(job.MaxAttempts));
            Fact(html, "Категория отказа", job.FailureCategory);
            Fact(html, "Доступно с", job.AvailableAt);
            html.Append("</dl>\n");

            if (job.Attempts.Count == 0)
            {
                html.Append("<p class=\"empty\">Попыток не зафиксировано.</p>\n");
                continue;
            }

            html.Append("<div class=\"scroll\">\n<table class=\"data\"><thead><tr><th scope=\"col\" class=\"num\">№</th>")
                .Append("<th scope=\"col\">Начата</th><th scope=\"col\">Завершена</th><th scope=\"col\" class=\"num\">Время, мс</th>")
                .Append("<th scope=\"col\" class=\"num\">Запросов</th><th scope=\"col\" class=\"num\">Резерв</th>")
                .Append("<th scope=\"col\" class=\"num\">Вход</th><th scope=\"col\" class=\"num\">Выход</th>")
                .Append("<th scope=\"col\" class=\"num\">Кэш</th><th scope=\"col\">Категория отказа</th></tr></thead>\n<tbody>\n");
            foreach (var attempt in job.Attempts)
            {
                html.Append("<tr><td class=\"num\">").Append(Number(attempt.Number))
                    .Append("</td><td>").Append(Escape(attempt.StartedAt))
                    .Append("</td><td>").Append(Escape(attempt.FinishedAt ?? "не завершена"))
                    .Append("</td><td class=\"num\">").Append(attempt.ElapsedMilliseconds is { } elapsed ? Number(elapsed) : "—")
                    .Append("</td><td class=\"num\">").Append(Number(attempt.MeasuredRequests))
                    .Append("</td><td class=\"num\">").Append(Number(attempt.ReservedRequests))
                    .Append("</td><td class=\"num\">").Append(attempt.InputTokens is { } input ? Number(input) : "—")
                    .Append("</td><td class=\"num\">").Append(attempt.OutputTokens is { } output ? Number(output) : "—")
                    .Append("</td><td class=\"num\">").Append(attempt.CachedInputTokens is { } cached ? Number(cached) : "—")
                    .Append("</td><td>").Append(Escape(attempt.FailureCategory ?? "—")).Append("</td></tr>\n");
            }

            html.Append("</tbody></table>\n</div>\n");
        }
    }

    private static void AppendOutside(StringBuilder html, ReportDocument document)
    {
        if (document.PostsOutsideWindow.Count == 0)
        {
            return;
        }

        html.Append("<section id=\"outside\" class=\"card\">\n<h2>Посты вне снимка</h2>\n")
            .Append("<p class=\"note\">Эти посты увидены другим сканом и в снимок не входят: их решения и анкеты ")
            .Append("не относятся к выбранному снимку и в счётчики не попали.</p>\n")
            .Append("<div class=\"scroll\">\n<table class=\"data\"><thead><tr><th scope=\"col\">Номер</th>")
            .Append("<th scope=\"col\">Ссылка</th><th scope=\"col\">Виден</th><th scope=\"col\">Фильтр</th>")
            .Append("<th scope=\"col\">Адресация</th><th scope=\"col\">Исключён</th></tr></thead>\n<tbody>\n");
        foreach (var post in document.PostsOutsideWindow)
        {
            html.Append("<tr><td>").Append(Escape(post.Number)).Append("</td><td class=\"wrap-cell\">");
            if (post.OriginLink is { } link)
            {
                html.Append("<a href=\"").Append(Escape(link)).Append("\" rel=\"noreferrer noopener\" target=\"_blank\">оригинал</a>");
            }
            else
            {
                html.Append('—');
            }

            html.Append("</td><td>").Append(Escape(post.LastSeenAt)).Append("</td><td>")
                .Append(Escape(post.Filter.ReasonLabel)).Append("</td><td>")
                .Append(Escape(post.Filter.AddressingLabel)).Append("</td><td>")
                .Append(post.ModerationExcluded ? "да" : "нет").Append("</td></tr>\n");
        }

        html.Append("</tbody></table>\n</div>\n</section>\n");
    }

    private static void Row(StringBuilder html, string label, string? value, string? note) =>
        html.Append("<tr><th scope=\"row\">").Append(Escape(label)).Append("</th><td class=\"wrap-cell\">")
            .Append(Escape(value ?? "—")).Append("</td><td class=\"wrap-cell note\">").Append(Escape(note ?? string.Empty))
            .Append("</td></tr>\n");

    private static void Metric(StringBuilder html, string label, int value) =>
        html.Append("<tr><th scope=\"row\">").Append(Escape(label)).Append("</th><td class=\"num\">")
            .Append(Number(value)).Append("</td></tr>\n");

    private static void Fact(StringBuilder html, string label, string? value) =>
        html.Append("<div><dt>").Append(Escape(label)).Append("</dt><dd>").Append(Escape(value ?? "—")).Append("</dd></div>\n");

    private static void Select(StringBuilder html, string id, string label, string empty, IReadOnlyList<(string Value, string Text)> options)
    {
        html.Append("<label class=\"field\">").Append(Escape(label)).Append("<select id=\"").Append(Escape(id)).Append("\">")
            .Append("<option value=\"\">").Append(Escape(empty)).Append("</option>");
        foreach (var option in options)
        {
            html.Append("<option value=\"").Append(Escape(option.Value)).Append("\">").Append(Escape(option.Text)).Append("</option>");
        }

        html.Append("</select></label>\n");
    }

    private static IReadOnlyList<(string Value, string Text)> Distinct(IEnumerable<(string Value, string Text)> values) =>
        [.. values
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.First().Text))];

    private static string JobState(ReportPostView post)
    {
        return post.Llm.CurrentJobState ?? "none";
    }

    private static string JobStateLabel(ReportPostView post) => JobState(post) switch
    {
        "none" => "задания модели нет",
        "pending" => "ожидает обработки",
        "running" => "выполняется",
        "succeeded" => "завершено",
        "failed" => "технический отказ",
        var other => other,
    };

    private static string Tone(string stage) => stage switch
    {
        ReportComposer.StageProfile => "pos",
        ReportComposer.StageNotProfile => "neg",
        ReportComposer.StageUncertain => "warn",
        ReportComposer.StagePending or ReportComposer.StageRunning => "wait",
        ReportComposer.StageFailed => "bad",
        _ => "idle",
    };

    /// <summary>
    /// Итог этапа для карточки. У поста, исключённого модерацией, причина недопуска называется
    /// прямо в бейдже: рядом с решением фильтра «допущен» иначе читалось бы двусмысленно.
    /// </summary>
    private static string StageBadge(ReportPostView post) =>
        post.ModerationExcluded && !post.Filter.AdmittedToLlm
            ? post.StageLabel + ": исключён модерацией"
            : post.StageLabel;

    private static string Share(ReportCounter counter)
    {
        if (counter.Kind != ReportCounterKind.Share)
        {
            return "—";
        }

        return counter.Percent is { } percent
            ? percent.ToString("0.0", CultureInfo.InvariantCulture) + " %"
            : "доля не определена: знаменатель 0";
    }

    /// <summary>Время в UTC без зависимости от часового пояса читателя.</summary>
    private static string Moment(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString(MomentFormat, CultureInfo.InvariantCulture);

    private static string MomentOr(DateTimeOffset? moment) => moment is { } value ? Moment(value) : "—";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Number(int value) => Number((long)value);

    /// <summary>
    /// Имя файла из пути, записанного на любой платформе: отчёт переносим, а путь
    /// метаданных однажды мог быть получен в Windows-окружении и отрисован в Linux.
    /// </summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>
    /// Смещение и длина в координатах UTF-16. Группировка разрядов здесь запрещена: «2128+134»
    /// читается однозначно, а «2,128+134» похоже на два разных числа.
    /// </summary>
    private static string Offset(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Offset(int value) => Offset((long)value);

    private static string Hash(string content, string algorithm) =>
        algorithm + "-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Приводит встроенный блок к виду, который получает HTML-парсер: браузер заменяет
    /// CRLF на LF до проверки политикой, поэтому хеш считается по нормализованному тексту.
    /// </summary>
    private static string NormalizeNewlines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// Экранирование текста и значений атрибутов: активная разметка из данных сообщений
    /// не может стать частью документа.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = new StringBuilder(value.Length + 16);
        foreach (var symbol in value)
        {
            switch (symbol)
            {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\'':
                    escaped.Append("&#39;");
                    break;
                default:
                    escaped.Append(symbol);
                    break;
            }
        }

        return escaped.ToString();
    }
}