using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Tests;

public sealed class DvachTests
{
    private static readonly DvachThread Thread = new("soc", 100000);

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "dvach", "thread.json");

    [Fact]
    public async Task AnonymizedThreadPreservesMarkupDecisionsAndContactValues()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture, TestContext.Current.CancellationToken);
        var snapshot = DvachSnapshot.Parse(bytes, Thread);
        (string Reply, string Contacts)[] expected =
        [
            ("op", ""),
            ("none", ""),
            ("none", "telegram:fixture_test_25_zq"),
            ("none", "discord:fixture_test_26_zq"),
            ("op_reply", "telegram:fixture_test_35_zq"),
            ("none", ""),
            ("none", "email:Fixture.Test.50@example.invalid"),
            ("op_reply", ""),
            ("op_reply", "telegram:fixture_test_77_zq"),
            ("none", "telegram:fixture_test_79_zq"),
            ("none", ""),
            ("none", "telegram:fixture_test_117_zq"),
            ("none", "discord:fixture_test_146_zq"),
            ("none", ""),
            ("none", "discord:fixture_236_zq"),
            ("other_reply", ""),
            ("none", "email:Mailbox326@example.invalid"),
            ("none", ""),
            ("other_reply", ""),
            ("op_reply", "telegram:fixture_test_395_zq"),
            ("op_reply", ""),
            ("op_reply", ""),
            ("none", "email:fixture.test.445@example.invalid"),
            ("none", ""),
            ("op_reply", "telegram:fixture_test_504_zq"),
            ("other_reply", ""),
            ("none", ""),
            ("none", "telegram:fixture_test_622_zq,discord:fixture_discord_622_zq"),
            ("none", "telegram:fixture_test_624_zq"),
            ("none", "telegram:fixture_test_639_zq"),
            ("other_reply", ""),
        ];

        Assert.Equal(expected.Length, snapshot.Posts.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var sourcePost = snapshot.Posts[i];
            var prepared = DvachFilter.Prepare(Thread, sourcePost, Thread.SourceKey);
            var (reply, contacts) = expected[i];
            Assert.Equal(reply, prepared.Reply);
            var reason = reply == "op" ? "opening_post" : reply == "other_reply" ? "reply_to_other_post"
                : contacts.Length == 0 ? "no_own_contact" : "candidate";
            Assert.Equal(reason, prepared.Reason);
            Assert.Equal(contacts, string.Join(',', prepared.Contacts.Select(c => $"{c.Platform}:{c.NormalizedValue}")));
            Assert.False(prepared.Ambiguous);
            Assert.Equal(sourcePost.Html, prepared.Post.Content);
            Assert.Equal(Thread.Url + "#" + prepared.Post.ExternalId, prepared.Post.Url);
            Assert.All(prepared.Contacts, c => Assert.StartsWith("dvach:body:", c.Provenance, StringComparison.Ordinal));
        }

        var quoted = DvachFilter.Prepare(Thread, snapshot.Posts[17], Thread.SourceKey);
        using var metadata = JsonDocument.Parse(quoted.Post.RetrievalMetadata);
        Assert.Contains("fixture_quote_360_zq", metadata.RootElement.GetProperty("quotes").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture_quote_360_zq", metadata.RootElement.GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedReplyAndForeignContactStaySeparateFromOwnContacts()
    {
        const string html = """
            <blockquote>
              <a class="post-reply-link" href="/soc/res/100000.html#100001" data-thread="100000" data-num="100001">&gt;&gt;100001</a>
              <a href="https://t.me/fixture_quoted_zq">чужой контакт</a>
            </blockquote><br><script>xmpp:script@hidden.invalid</script>
            <div hidden>tg @fixture_hidden_zq</div>
            &gt; Тг @fixture_quote_zq<br>
            Мой Telegram: @Fixture_One_zq, @Fixture_Two_zq<br>
            Контакты: дискорд &quot;FixtureOld#1234&quot;<br>
            Пиши мне: MailTo:Case.Sensitive@EXAMPLE.INVALID<br>
            Jabber: Mixed.Case@EXAMPLE.INVALID<br>
            <a href="https://t.me/fixture_href_zq">сюда</a>
            """;
        var prepared = Prepare(html);
        Assert.Equal("none", prepared.Reply);
        Assert.Equal("candidate", prepared.Reason);
        Assert.True(prepared.Ambiguous);
        Assert.Equal(
            ["fixture_one_zq", "fixture_two_zq", "FixtureOld#1234", "Case.Sensitive@example.invalid",
             "mixed.case@example.invalid", "fixture_href_zq"],
            prepared.Contacts.Select(c => c.NormalizedValue));
        Assert.Equal(["telegram", "telegram", "discord", "email", "xmpp", "telegram"],
            prepared.Contacts.Select(c => c.Platform));
        Assert.Equal("@Fixture_One_zq", prepared.Contacts[0].RawValue);
        Assert.EndsWith(":href", prepared.Contacts[^1].Provenance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@fixture_unspecified_zq")]
    [InlineData("Мне переслали https://t.me/fixture_other_zq")]
    [InlineData("Тг: мне переслали https://t.me/fixture_other_zq")]
    [InlineData("Telegram: user@example.invalid")]
    [InlineData("Discord https://discord.gg/fixture_invite_zq")]
    [InlineData("https://t.me/+fixture_invite_zq")]
    [InlineData("https://t.me/fixture_channel_zq/44")]
    [InlineData("https://t.me/share")]
    [InlineData("Email: fixture@localhost")]
    [InlineData("Discord: fixture..test")]
    public void NonPersonalOrUnattributedTokensAreNotContacts(string html)
    {
        var prepared = Prepare(html);
        Assert.Equal("no_own_contact", prepared.Reason);
        Assert.Empty(prepared.Contacts);
    }

    [Theory]
    [InlineData(404, "thread_unavailable")]
    [InlineData(410, "thread_unavailable")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "http_error")]
    [InlineData(302, "http_error")]
    [InlineData(206, "incomplete_response")]
    public async Task FetchRejectsUnusableHttpStatusesAndReadsRetryAfter(int status, string category)
    {
        var clock = new Support.ManualTimeProvider();
        using var client = Client((request, _) =>
        {
            Assert.Equal("https://2ch.org/soc/res/100000.json", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return Task.FromResult(response);
        });
        var adapter = new DvachClient(client, clock);
        var error = await Assert.ThrowsAsync<SourceFailure>(() => adapter.FetchAsync(Thread, new(), null, TestContext.Current.CancellationToken));
        Assert.Equal(category, error.Category);
        Assert.Equal("fetch", error.Phase);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), error.RetryAt);
    }

    [Theory]
    [InlineData("text/html", "{}", 1024, "unexpected_content_type")]
    [InlineData("application/json", "{", 1024, "invalid_json")]
    [InlineData("application/json", "{}", 1024, "invalid_json")]
    [InlineData("application/json", "{\"x\":\"long\"}", 5, "response_too_large")]
    public async Task FetchRejectsMalformedOrOversizedResponses(string mediaType, string payload, int limit, string category)
    {
        using var client = Client((_, _) =>
        {
            var content = new StringContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = new DvachClient(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<SourceFailure>(() => adapter.FetchAsync(
            Thread, new() { MaxResponseBytes = limit }, null, TestContext.Current.CancellationToken));
        Assert.Equal(category, error.Category);
    }

    [Fact]
    public async Task SnapshotChecksCountsAndDistinguishesEmptyShape()
    {
        var json = await File.ReadAllTextAsync(Fixture, TestContext.Current.CancellationToken);
        var partial = json.Replace("\"posts_count\": 31", "\"posts_count\": 32", StringComparison.Ordinal);
        var error = Assert.Throws<SourceFailure>(() => DvachSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(partial), Thread));
        Assert.Equal("incomplete_snapshot", error.Category);
        Assert.Empty(DvachSnapshot.Parse("""
            {"board":{"id":"soc"},"current_thread":100000,"is_board":false,"is_index":false,
            "is_closed":0,"max_num":0,"posts_count":0,"threads":[{"posts":[]}]}
            """u8, Thread).Posts);
    }

    [Fact]
    public async Task RequestDeadlineIsDistinctFromCallerCancellation()
    {
        using var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = new DvachClient(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<SourceFailure>(() => adapter.FetchAsync(
            Thread, new() { RequestTimeoutSeconds = 1 }, null, TestContext.Current.CancellationToken));
        Assert.Equal("timeout", error.Category);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync(Thread, new(), null, new CancellationToken(true)));
    }

    [Theory]
    [InlineData("Application/JSON; charset=utf-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json")]
    public async Task FetchAcceptsHttpEquivalentJsonContentTypes(string contentType)
    {
        var payload = await File.ReadAllBytesAsync(Fixture, TestContext.Current.CancellationToken);
        using var client = Client((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            // HTTP сравнивает тип и кодировку без учёта регистра, а кавычки в charset эквивалентны.
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = new DvachClient(client, TimeProvider.System);
        var snapshot = await adapter.FetchAsync(Thread, new(), null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(snapshot.Posts);
    }

    [Fact]
    public async Task DuplicateSnapshotCountersAreRejected()
    {
        var json = (await File.ReadAllTextAsync(Fixture, TestContext.Current.CancellationToken))
            .Replace("\"posts_count\": 31", "\"posts_count\": 32, \"posts_count\": 31", StringComparison.Ordinal);
        using var client = Client((_, _) =>
        {
            var content = new StringContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var adapter = new DvachClient(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<SourceFailure>(() => adapter.FetchAsync(
            Thread, new(), null, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_json", error.Category);
    }

    [Fact]
    public void ContactLinkWhoseCaptionIsOnlyLeadIsRecognized()
    {
        var prepared = Prepare("""<a href="https://t.me/fixture_caption_zq">для связи</a>""");
        Assert.Equal("candidate", prepared.Reason);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal("telegram", contact.Platform);
        Assert.Equal("fixture_caption_zq", contact.NormalizedValue);
        Assert.EndsWith(":href", contact.Provenance, StringComparison.Ordinal);
        // Тот же текст без ссылки контактом не становится.
        Assert.Empty(Prepare("Для связи").Contacts);
    }

    [Fact]
    public void SlashSeparatedContactListYieldsEveryAddress()
    {
        var prepared = Prepare("Контакты: first@example.invalid/second@example.invalid");
        Assert.Equal("candidate", prepared.Reason);
        Assert.Equal(["first@example.invalid", "second@example.invalid"],
            prepared.Contacts.Select(c => c.NormalizedValue));
    }

    [Fact]
    public void BlockquoteEndsTheAuthorLineBeforeFurtherText()
    {
        var prepared = Prepare("Тг: @fixture_handle_zq<blockquote>цитата</blockquote>Ещё текст");
        Assert.Equal("candidate", prepared.Reason);
        Assert.Equal("fixture_handle_zq", Assert.Single(prepared.Contacts).NormalizedValue);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        Assert.Equal("Тг: @fixture_handle_zq\nЕщё текст", metadata.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void LinkCaptionIsTheVisibleTextOfThatLink()
    {
        var hidden = Prepare("""<a href="https://t.me/fixture_hidden_zq"><span hidden>сюда</span></a>сюда""");
        Assert.Equal("no_own_contact", hidden.Reason);
        Assert.Empty(hidden.Contacts);
        var visible = Prepare("""<a href="https://t.me/fixture_visible_zq">сюда</a>""");
        Assert.Equal("fixture_visible_zq", Assert.Single(visible.Contacts).NormalizedValue);
    }

    [Fact]
    public void ContactCoordinatesPointIntoTheSerializedText()
    {
        var prepared = Prepare("<br>  Тг: @fixture_handle_zq");
        var contact = Assert.Single(prepared.Contacts);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        var lines = metadata.RootElement.GetProperty("text").GetString()!.Split('\n');
        var parts = contact.Provenance.Split(':');
        var line = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var offset = int.Parse(parts[3], CultureInfo.InvariantCulture);
        Assert.Equal("@fixture_handle_zq", lines[line - 1][offset..]);
    }

    private static PreparedPost Prepare(string html) =>
        DvachFilter.Prepare(Thread, new DvachPost(100099, Thread.Number, html, null), Thread.SourceKey);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new TestHandler(send));

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}