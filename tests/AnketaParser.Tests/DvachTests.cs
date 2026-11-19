using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using AnketaParser.Infrastructure.Sources;

namespace AnketaParser.Tests;

public sealed class DvachTests
{
    private static readonly DvachThread Thread = new("soc", 100000);

    // Минимальный согласованный снимок для проверок счётчиков и журнала фикстуры.
    private const string SinglePostSnapshot = """
        {"board":{"id":"soc"},"current_thread":100000,"is_board":false,"is_index":false,
        "is_closed":0,"max_num":100000,"posts_count":1,
        "threads":[{"posts":[{"board":"soc","num":100000,"number":1,"parent":0,"comment":"Текст","timestamp":0}]}]}
        """;

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "dvach", "thread.json");

    [Fact]
    public async Task AnonymizedThreadPreservesMarkupDecisionsAndContactValues()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture, TestContext.Current.CancellationToken);
        var snapshot = DvachSnapshot.Parse(bytes, Thread);
        (string Reply, string Contacts, bool Ambiguous)[] expected =
        [
            ("op", "", false),
            ("none", "", false),
            ("none", "telegram:fixture_test_25_zq", false),
            ("none", "discord:fixture_test_26_zq", false),
            ("op_reply", "telegram:fixture_test_35_zq", false),
            ("none", "telegram:fixture_test_43_zq", false),
            ("none", "email:Fixture.Test.50@example.invalid", false),
            ("op_reply", "telegram:fixture_test_57_zq", false),
            ("op_reply", "telegram:fixture_test_77_zq", false),
            ("none", "telegram:fixture_test_79_zq", false),
            ("none", "telegram:fixture_test_101_zq", false),
            ("none", "telegram:fixture_test_117_zq", false),
            ("none", "discord:fixture_test_146_zq", false),
            ("none", "", false),
            ("none", "discord:fixture_236_zq", false),
            ("other_reply", "", false),
            ("none", "email:Mailbox326@example.invalid", false),
            ("none", "", false),
            ("other_reply", "", false),
            ("op_reply", "telegram:fixture_test_395_zq", false),
            ("op_reply", "telegram:fixture_test_411_zq", false),
            ("op_reply", "discord:fixture_427_zq", false),
            ("none", "email:fixture.test.445@example.invalid", false),
            ("none", "telegram:fixture_452_one_zq,telegram:fixture_452_two_zq,telegram:fixture_452_three_zq", true),
            ("op_reply", "telegram:fixture_test_504_zq", false),
            ("other_reply", "telegram:fixture_521_one_zq,telegram:fixture_521_two_zq,telegram:fixture_521_three_zq,telegram:fixture_521_four_zq", true),
            ("none", "telegram:fixture_573_zq", false),
            ("none", "telegram:fixture_test_622_zq,discord:fixture_discord_622_zq", false),
            ("none", "telegram:fixture_test_624_zq", false),
            ("none", "telegram:fixture_test_639_zq", false),
            ("other_reply", "", false),
        ];


        Assert.Equal(expected.Length, snapshot.Posts.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var sourcePost = snapshot.Posts[i];
            var prepared = DvachFilter.Prepare(Thread, sourcePost, Thread.SourceKey);
            var (reply, contacts, ambiguous) = expected[i];
            Assert.Equal(reply, prepared.Reply);
            var reason = reply == "op" ? "opening_post" : contacts.Length == 0 ? "no_own_contact" : "candidate";
            Assert.Equal(reason, prepared.Reason);
            Assert.Equal(contacts, string.Join(',', prepared.Contacts.Select(c => $"{c.Platform}:{c.NormalizedValue}")));
            Assert.Equal(ambiguous, prepared.Ambiguous);
            Assert.Equal(sourcePost.Html, prepared.Post.Content);
            // Идентификатор и адрес сверяются с номером исходного сообщения фикстуры:
            // ожидание не выводится из проверяемого результата фильтра.
            var expectedId = sourcePost.Number.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(expectedId, prepared.Post.ExternalId);
            Assert.Equal(Thread.Url + "#" + expectedId, prepared.Post.Url);
            Assert.All(prepared.Contacts, c => Assert.StartsWith("dvach:body:", c.Provenance, StringComparison.Ordinal));
        }

        var quoted = DvachFilter.Prepare(Thread, snapshot.Posts[17], Thread.SourceKey);
        using var metadata = JsonDocument.Parse(quoted.Post.RetrievalMetadata);
        Assert.Contains("fixture_quote_360_zq", metadata.RootElement.GetProperty("quotes").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture_quote_360_zq", metadata.RootElement.GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyntheticHoldoutHasIndependentLabelsAndIdenticalDuplicateInputs()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "llm");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "holdout-thread.json"), TestContext.Current.CancellationToken);
        var thread = new DvachThread("soc", 220000);
        var snapshot = DvachSnapshot.Parse(bytes, thread);
        using var labels = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "holdout-labels.json"), TestContext.Current.CancellationToken));
        var labeled = labels.RootElement.EnumerateArray().ToArray();
        Assert.Equal(snapshot.Posts.Count - 1, labeled.Length);
        Assert.Equal(labeled.Length, labeled.Select(item => item.GetProperty("post").GetInt64()).Distinct().Count());
        var posts = snapshot.Posts.Skip(1).ToDictionary(post => post.Number);
        var preparedTexts = new Dictionary<long, string>();
        foreach (var label in labeled)
        {
            var number = label.GetProperty("post").GetInt64();
            var prepared = DvachFilter.Prepare(thread, posts[number], thread.SourceKey);
            Assert.Equal("candidate", prepared.Reason);
            using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
            var text = metadata.RootElement.GetProperty("text").GetString()!;
            preparedTexts[number] = AnketaParser.Infrastructure.Llm.LlmInput.Redact(text,
                prepared.Contacts.SelectMany(contact => new[] { contact.RawValue, contact.NormalizedValue }));
        }
        var unique = labeled.Where(item => item.GetProperty("independent").GetBoolean()).Select(item => item.GetProperty("post").GetInt64()).ToArray();
        Assert.Equal(unique.Length, unique.Select(number => preparedTexts[number]).Distinct(StringComparer.Ordinal).Count());
        foreach (var duplicate in labeled.Where(item => !item.GetProperty("independent").GetBoolean()))
            Assert.Equal(preparedTexts[duplicate.GetProperty("duplicateOf").GetInt64()], preparedTexts[duplicate.GetProperty("post").GetInt64()]);
    }

    [Fact]
    public void StructuralQuotesStaySeparateFromAuthorContacts()
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
    [InlineData("Discord https://discord.gg/fixture_invite_zq")]
    [InlineData("https://t.me/+fixture_invite_zq")]
    [InlineData("https://t.me/fixture_channel_zq/44")]
    [InlineData("https://t.me/share")]
    [InlineData("Email: fixture@localhost")]
    [InlineData("Discord: fixture..test")]
    public void UnsupportedAndInvalidTokensAreNotContacts(string html)
    {
        var prepared = Prepare(html);
        Assert.Equal("no_own_contact", prepared.Reason);
        Assert.Empty(prepared.Contacts);
    }

    [Theory]
    [InlineData("@sampleuser42", "telegram", "sampleuser42")]
    [InlineData("Привет, пиши в тг @sampleuser42, буду рад", "telegram", "sampleuser42")]
    [InlineData("Тг<br>@sampleuser42", "telegram", "sampleuser42")]
    [InlineData("писать в тг sampleuser42", "telegram", "sampleuser42")]
    [InlineData("sampleuser42 телеграмм", "telegram", "sampleuser42")]
    [InlineData("Tг sampleuser42", "telegram", "sampleuser42")]
    [InlineData("Тн: sampleuser42", "telegram", "sampleuser42")]
    [InlineData("Дис sampleuser42", "discord", "sampleuser42")]
    [InlineData("Дискорд для связи: sampleuser42", "discord", "sampleuser42")]
    [InlineData("Discord: Sample.Name", "discord", "Sample.Name")]
    [InlineData("Напиши в Discord Sample.Name", "discord", "Sample.Name")]
    [InlineData("Email: Test.User@example.invalid и ещё текст", "email", "Test.User@example.invalid")]
    [InlineData("тгшку:Test.User@example.invalid", "email", "Test.User@example.invalid")]
    [InlineData("Telegram: Test.User@example.invalid", "email", "Test.User@example.invalid")]
    public void TechnicalContactsAreIndependentOfPositionAndMeaning(string html, string platform, string value)
    {
        var prepared = Prepare(html);
        Assert.Equal("candidate", prepared.Reason);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal(platform, contact.Platform);
        Assert.Equal(value, contact.NormalizedValue);
    }

    [Theory]
    [InlineData("Мне переслали @sampleuser42")]
    [InlineData("Мне переслали https://t.me/sampleuser42")]
    [InlineData("Контакт моего друга: @sampleuser42")]
    [InlineData("Чужой контакт: <a href='https://t.me/sampleuser42'>ссылка</a>")]
    public void ExplicitThirdPartyContactsAreNotTreatedAsAuthorContacts(string html) =>
        Assert.Empty(Prepare(html).Contacts);

    [Fact]
    public void ForeignContactDoesNotSuppressSeparateAuthorContact()
    {
        var prepared = Prepare("Контакт друга: @sampleuser42. Мой тг: @ownuser42");
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal("ownuser42", contact.NormalizedValue);
    }

    [Theory]
    [InlineData("a@sampleuser42", "ambiguous_at", "a@sampleuser42")]
    [InlineData("tg @ sampleюser42", "mixed_alphabet", "@ sampleюser42")]
    [InlineData("sampleuser42 дс", "discord_reverse", "sampleuser42")]
    public void UnresolvedContactsKeepOriginalCoordinatesWithoutInventingAContact(string html, string kind, string raw)
    {
        var prepared = Prepare(html);
        Assert.Equal(DvachFilter.UnresolvedContact, prepared.Reason);
        Assert.Empty(prepared.Contacts);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        var text = metadata.RootElement.GetProperty("text").GetString()!;
        var possible = Assert.Single(metadata.RootElement.GetProperty("possibleContacts").EnumerateArray());
        Assert.Equal(kind, possible.GetProperty("Kind").GetString());
        Assert.Equal(1, possible.GetProperty("Line").GetInt32());
        Assert.Equal(raw, text.Substring(possible.GetProperty("Offset").GetInt32(), possible.GetProperty("Length").GetInt32()));
    }

    [Theory]
    [InlineData("Тг@SampleUser42", "@SampleUser42")]
    [InlineData("ТГ: @ sampleuser42", "@ sampleuser42")]
    [InlineData("@ sampleuser42", "@ sampleuser42")]
    [InlineData("4.@sampleuser42", "@sampleuser42")]
    public void RecognizesTelegramSeparatorsWithoutLosingSourceSpan(string html, string raw)
    {
        var prepared = Prepare(html);
        Assert.Equal("candidate", prepared.Reason);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal(raw, contact.RawValue);
        Assert.Equal("sampleuser42", contact.NormalizedValue);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        var text = metadata.RootElement.GetProperty("text").GetString()!;
        var coordinate = contact.Provenance.Split(':');
        var offset = int.Parse(coordinate[3], CultureInfo.InvariantCulture);
        Assert.Equal(raw, text.Substring(offset, raw.Length));
        var masked = AnketaParser.Infrastructure.Llm.LlmInput.Redact(text, [contact.RawValue, contact.NormalizedValue]);
        Assert.DoesNotContain("sampleuser42", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text.Length, masked.Length);
    }

    [Theory]
    [InlineData("@samp leuser42 (без пробела)", "@samp leuser42")]
    [InlineData("тг @ sam ple_user42 (минус пробелы)", "@ sam ple_user42")]
    public void JoinsSpacesOnlyWhenAuthorExplicitlyRequestsIt(string text, string raw)
    {
        var prepared = Prepare(text);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal("sampleuser42", contact.NormalizedValue.Replace("_", "", StringComparison.Ordinal));
        Assert.Equal(raw, contact.RawValue);
        var masked = AnketaParser.Infrastructure.Llm.LlmInput.Redact(text, [contact.RawValue, contact.NormalizedValue]);
        Assert.DoesNotContain(raw, masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mail@example.invalid")]
    [InlineData("https://example.invalid/4.@sampleuser42")]
    [InlineData("тг @ sampleuser42я")]
    [InlineData("a@sampleuser42")]
    [InlineData("тн sampleuser42")]
    [InlineData("Тн: sampleuser42/path")]
    public void TelegramSeparatorsDoNotAcceptPartialOrForeignTokens(string text) =>
        Assert.DoesNotContain(Prepare(text).Contacts, contact => contact.Platform == "telegram");

    [Theory]
    [InlineData("profile telegram: @sampleuser42")]
    [InlineData("write telegram @sampleuser42")]
    [InlineData("support тг: sampleuser42")]
    public void LabeledValueDoesNotRepeatTheWordBeforeItsLabel(string html)
    {
        // Прямое правило уже связало метку со значением справа: обратное правило
        // не должно сообщать слово перед меткой как второй контакт.
        var prepared = Prepare(html);
        Assert.Equal("candidate", prepared.Reason);
        Assert.False(prepared.Ambiguous);
        Assert.Equal("sampleuser42", Assert.Single(prepared.Contacts).NormalizedValue);
    }

    [Fact]
    public void SlashSeparatedLabeledListYieldsEveryTelegramAddress()
    {
        var prepared = Prepare("Контакты: telegram: firstuser42/seconduser42/thirduser42");
        Assert.Equal("candidate", prepared.Reason);
        Assert.Equal(["firstuser42", "seconduser42", "thirduser42"],
            prepared.Contacts.Select(contact => contact.NormalizedValue));
        // Слэш как часть пути не открывает список и не даёт контакт.
        Assert.Empty(Prepare("Профиль: telegram: alice_name/path").Contacts);
    }

    [Fact]
    public void EmailRightOfLabelDoesNotSuppressTelegramReverseMatch()
    {
        // Подавление обратного совпадения относится только к контакту той же платформы.
        var prepared = Prepare("alice_name тг; Mailbox@example.invalid");
        Assert.Equal(["telegram:alice_name", "email:Mailbox@example.invalid"],
            prepared.Contacts.Select(contact => $"{contact.Platform}:{contact.NormalizedValue}"));
    }

    [Fact]
    public void EmailHrefWithoutMailtoIsNotAContact()
    {
        // `alice@example.invalid` в href — относительный путь, а не почтовая ссылка.
        Assert.Empty(Prepare("""Связаться <a href="alice@example.invalid">письмо</a>""").Contacts);
        // Явный mailto остаётся контактом.
        Assert.Single(Prepare("""Связаться <a href="mailto:alice@example.invalid">письмо</a>""").Contacts);
    }

    [Fact]
    public void QuotedReplyLinkCaptionStaysInsideTheQuote()
    {
        var prepared = Prepare("""
            &gt; цитата <a class="post-reply-link" href="/soc/res/100000.html#100001" data-thread="100000" data-num="100001">&gt;&gt;100001<br>@fixture_hidden_zq</a>
            """);
        // Перенос внутри подписи цитатной ссылки не открывает авторскую строку.
        Assert.Empty(prepared.Contacts);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        Assert.Contains("fixture_hidden_zq", metadata.RootElement.GetProperty("quotes").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture_hidden_zq", metadata.RootElement.GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitContactRightOfLabelSuppressesReverseMatch()
    {
        var prepared = Prepare("write telegram: https://t.me/alice_name");
        Assert.Equal("candidate", prepared.Reason);
        Assert.False(prepared.Ambiguous);
        Assert.Equal("alice_name", Assert.Single(prepared.Contacts).NormalizedValue);
    }

    [Theory]
    [InlineData("Тг: firstuser42, seconduser42", "telegram", "firstuser42", "seconduser42")]
    [InlineData("Discord: First.Name / Second.Name", "discord", "First.Name", "Second.Name")]
    public void LabeledListsPreserveAllValues(string text, string platform, string first, string second)
    {
        var prepared = Prepare(text);
        Assert.Equal("candidate", prepared.Reason);
        Assert.True(prepared.Ambiguous);
        Assert.Equal([first, second], prepared.Contacts.Select(contact => contact.NormalizedValue));
        Assert.All(prepared.Contacts, contact => Assert.Equal(platform, contact.Platform));
    }

    [Fact]
    public void MixedContactsAndCoordinatesRemainComplete()
    {
        var prepared = Prepare("<br>Тг: @sampleuser42 или почта Test.User@example.invalid<br>Контакты: @otheruser42 / Test.Two@example.invalid / xmpp:test@example.invalid");
        Assert.Equal("candidate", prepared.Reason);
        Assert.True(prepared.Ambiguous);
        Assert.Equal(["telegram:sampleuser42", "email:Test.User@example.invalid", "telegram:otheruser42",
            "email:Test.Two@example.invalid", "xmpp:test@example.invalid"],
            prepared.Contacts.Select(c => $"{c.Platform}:{c.NormalizedValue}"));
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        var lines = metadata.RootElement.GetProperty("text").GetString()!.Split('\n');
        foreach (var contact in prepared.Contacts)
        {
            var parts = contact.Provenance.Split(':');
            var line = int.Parse(parts[2], CultureInfo.InvariantCulture);
            var offset = int.Parse(parts[3], CultureInfo.InvariantCulture);
            Assert.StartsWith(contact.RawValue, lines[line - 1][offset..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InlineLinksAndRepliesAreTechnicalSignals()
    {
        var prepared = Prepare("""
            <a class="post-reply-link" href="/soc/res/100000.html#99999" data-thread="100000" data-num="99999">&gt;&gt;99999</a>
            Связаться <a href="https://t.me/sampleuser42">сюда</a> или <a href="https://t.me/otheruser42">@otheruser42</a>.
            """);
        Assert.Equal("other_reply", prepared.Reply);
        Assert.Equal("candidate", prepared.Reason);
        Assert.Equal(["sampleuser42", "otheruser42"], prepared.Contacts.Select(c => c.NormalizedValue));
        Assert.EndsWith(":href", prepared.Contacts[0].Provenance, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        Assert.Single(metadata.RootElement.GetProperty("replies").EnumerateArray());
        var text = metadata.RootElement.GetProperty("text").GetString()!.Split('\n');
        var coordinate = prepared.Contacts[0].Provenance.Split(':');
        Assert.StartsWith("сюда", text[int.Parse(coordinate[2], CultureInfo.InvariantCulture) - 1]
            [int.Parse(coordinate[3], CultureInfo.InvariantCulture)..], StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenReplyLabelIsNotAVisibleReply()
    {
        // Подпись ответа берётся по видимому тексту, как и подпись обычной ссылки:
        // скрытая подпись не подтверждает адресный ответ.
        _ = Assert.Throws<SourceFailure>(() => Prepare("""
            <a class="post-reply-link" href="/soc/res/100000.html#99999" data-thread="100000" data-num="99999"><span hidden>&gt;&gt;99999</span></a>
            """));
    }

    [Fact]
    public void SlashListRejectsAddressRemainder()
    {
        // Остаток адреса не даёт ни первого, ни второго контакта.
        Assert.Empty(Prepare("Контакты: tg: alice_name/bobby_name:443").Contacts);
    }

    [Fact]
    public void ReplyLabelSplitAcrossLinesIsNotAVisibleReply()
    {
        // Видимая подпись `>>12` и `3` через перенос не равна номеру поста.
        _ = Assert.Throws<SourceFailure>(() => Prepare("""
            <a class="post-reply-link" href="/soc/res/100000.html#123" data-thread="100000" data-num="123">&gt;&gt;12<br>3</a>
            """));
    }

    [Theory]
    [InlineData("/path/@sampleuser42")]
    [InlineData("https://example.invalid/@sampleuser42")]
    [InlineData("https://example.invalid/user@example.invalid")]
    [InlineData("/path/user@example.invalid")]
    [InlineData("Тг<br>https://example.invalid/path")]
    [InlineData("<blockquote>@sampleuser42</blockquote><span hidden>@hiddenuser42</span>&gt; @quoteduser42")]
    public void PathsAndStructuralQuotesDoNotProduceContacts(string html) => Assert.Empty(Prepare(html).Contacts);

    [Fact]
    public void LongWhitespaceAfterPlatformDoesNotProduceAContact()
    {
        var prepared = Prepare("Тг" + new string(' ', 4000));
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
            Assert.Equal(AnketaParser.Core.ApplicationInfo.DefaultProductName, request.Headers.UserAgent.ToString());
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
    public void SnapshotChecksCountsAndDistinguishesEmptyShape()
    {
        // Явный документ не зависит от форматирования фикстуры: подстановка
        // счётчика не может молча оставить валидный снимок.
        var counted = SinglePostSnapshot.Replace("\"posts_count\":1", "\"posts_count\":2", StringComparison.Ordinal);
        var error = Assert.Throws<SourceFailure>(() => DvachSnapshot.Parse(
            System.Text.Encoding.UTF8.GetBytes(counted), Thread));
        Assert.Equal("incomplete_snapshot", error.Category);
        var single = Assert.Single(DvachSnapshot.Parse(
            System.Text.Encoding.UTF8.GetBytes(SinglePostSnapshot), Thread).Posts);
        Assert.Equal(Thread.Number, single.Number);
        Assert.Empty(DvachSnapshot.Parse("""
            {"board":{"id":"soc"},"current_thread":100000,"is_board":false,"is_index":false,
            "is_closed":0,"max_num":0,"posts_count":0,"threads":[{"posts":[]}]}
            """u8, Thread).Posts);
    }

    [Fact]
    public void JournalMatchesDetectsAnotherSnapshot()
    {
        var snapshot = DvachSnapshot.Parse(
            System.Text.Encoding.UTF8.GetBytes(SinglePostSnapshot), Thread);
        var journal = snapshot.Posts
            .Select(post => DvachFilter.Prepare(Thread, post, Thread.SourceKey))
            .ToArray();
        Assert.True(DvachIngestion.JournalMatches(journal, snapshot.Posts));

        var other = DvachSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(
            SinglePostSnapshot.Replace("\"comment\":\"Текст\"", "\"comment\":\"Другой текст\"", StringComparison.Ordinal)), Thread);
        Assert.False(DvachIngestion.JournalMatches(journal, other.Posts));
        var laterPost = DvachSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(
            SinglePostSnapshot.Replace("\"timestamp\":0", "\"timestamp\":1", StringComparison.Ordinal)), Thread);
        Assert.False(DvachIngestion.JournalMatches(journal, laterPost.Posts));
        Assert.False(DvachIngestion.JournalMatches(journal, []));
    }

    [Fact]
    public async Task RequestDeadlineIsDistinctFromCallerCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = new DvachClient(client, TimeProvider.System);
        var fetch = adapter.FetchAsync(
            Thread, new() { RequestTimeoutSeconds = 1 }, null, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<SourceFailure>(() => fetch);
        Assert.Equal("timeout", error.Category);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync(Thread, new(), null, new CancellationToken(true)));
    }

    [Fact]
    public async Task DuplicateSnapshotCountersAreRejected()
    {
        var json = SinglePostSnapshot.Replace(
            "\"posts_count\":1", "\"posts_count\":2, \"posts_count\":1", StringComparison.Ordinal);
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

    [Theory]
    [InlineData("Написать<br>сюда", "Перед ссылкой Написать\nсюда после")]
    [InlineData("Написать\nсюда", "Перед ссылкой Написать\nсюда после")]
    [InlineData("Написать<div>сюда</div>", "Перед ссылкой Написать\nсюда\n после")]
    public void ContactLinkAcrossLinesKeepsVisibleCoordinates(string caption, string expectedText)
    {
        var prepared = Prepare($"Перед ссылкой <a href=\"https://t.me/sampleuser42\">{caption}</a> после");
        Assert.Equal("candidate", prepared.Reason);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal("sampleuser42", contact.NormalizedValue);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        var text = metadata.RootElement.GetProperty("text").GetString();
        Assert.Equal(expectedText, text);
        var lines = text!.Split('\n');
        Assert.Equal("Перед ссылкой Написать", lines[0]);
        Assert.StartsWith("сюда", lines[1], StringComparison.Ordinal);
        var parts = contact.Provenance.Split(':');
        Assert.Equal("Написать", lines[int.Parse(parts[2], CultureInfo.InvariantCulture) - 1][int.Parse(parts[3], CultureInfo.InvariantCulture)..]);
        Assert.Equal("href", parts[4]);
    }

    [Theory]
    [InlineData("https://t.me/sampleuser42", "telegram", "https://t.me/sampleuser42", "sampleuser42")]
    [InlineData("mailto:Second@EXAMPLE.INVALID", "email", "mailto:Second@EXAMPLE.INVALID", "Second@example.invalid")]
    [InlineData("xmpp:Probe@EXAMPLE.INVALID", "xmpp", "xmpp:Probe@EXAMPLE.INVALID", "probe@example.invalid")]
    public void ContactHrefsAcrossLineBreakKeepValuesAndCoordinates(string href, string platform, string rawValue, string normalizedValue)
    {
        var prepared = Prepare($"Перед ссылкой <a href=\"{href}\">Написать<br>сюда</a> после");

        Assert.Equal("candidate", prepared.Reason);
        var contact = Assert.Single(prepared.Contacts);
        Assert.Equal(platform, contact.Platform);
        Assert.Equal(rawValue, contact.RawValue);
        Assert.Equal(normalizedValue, contact.NormalizedValue);
        Assert.Equal(0.9m, contact.Confidence);
        Assert.EndsWith(":href", contact.Provenance, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        Assert.Equal("Перед ссылкой Написать\nсюда после", metadata.RootElement.GetProperty("text").GetString());
        var parts = contact.Provenance.Split(':');
        var text = metadata.RootElement.GetProperty("text").GetString()!.Split('\n');
        Assert.Equal("Написать", text[int.Parse(parts[2], CultureInfo.InvariantCulture) - 1][int.Parse(parts[3], CultureInfo.InvariantCulture)..]);
    }

    [Theory]
    [InlineData("t.me/sampleuser42")]
    [InlineData("/t.me/sampleuser42")]
    public void HrefWithoutTelegramSchemeIsNotAContact(string href)
    {
        // Относительный адрес разрешается браузером относительно страницы треда:
        // такая ссылка не ведёт на Telegram и не делает пост кандидатом.
        var prepared = Prepare($"Связаться <a href=\"{href}\">профиль</a>");
        Assert.Equal("no_own_contact", prepared.Reason);
        Assert.Empty(prepared.Contacts);
    }

    [Fact]
    public void MultipleLinksAcrossLinesPreserveOrderAndIgnoreHiddenLinks()
    {
        var prepared = Prepare("""До <a href="https://t.me/sampleuser42">первая<br>ссылка</a> и <a href="mailto:second@example.invalid">вторая<br>ссылка</a><a href="https://t.me/hiddenuser42"><span hidden>скрыто</span></a> после""");
        Assert.Equal(["sampleuser42", "second@example.invalid"], prepared.Contacts.Select(c => c.NormalizedValue));
        Assert.All(prepared.Contacts, contact => Assert.EndsWith(":href", contact.Provenance, StringComparison.Ordinal));
        using var metadata = JsonDocument.Parse(prepared.Post.RetrievalMetadata);
        Assert.Equal("До первая\nссылка и вторая\nссылка после", metadata.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void LongLinesRemainOutsideContactRecognition()
    {
        var withContact = Prepare("first@example.invalid " + new string('x', 4096));
        Assert.Equal("no_own_contact", withContact.Reason);
        Assert.Empty(withContact.Contacts);
        var pathological = Prepare(new string('@', 8192));
        Assert.Equal("no_own_contact", pathological.Reason);
        Assert.Empty(pathological.Contacts);
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