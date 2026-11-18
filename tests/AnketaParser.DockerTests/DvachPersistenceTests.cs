using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace AnketaParser.DockerTests;

public sealed class DvachPersistenceTests(PostgresTestCluster cluster) : IClassFixture<PostgresTestCluster>
{
    private const long Thread = 100000;
    private const string Key = "https://2ch.org/soc/res/100000.html";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RevisingArchivingAndRevisitingPreservesHistoryAndSelectsCurrentJobs()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        var body = Payload("tg @fixture_first_zq", "Email: Another@EXAMPLE.INVALID");
        using var handler = new ThreadHandler((_, _) => Task.FromResult(Response(body)));
        await using var provider = CreateService(database, handler, TimeProvider.System);
        var scanner = provider.GetRequiredService<DvachIngestion>();

        var first = await scanner.ScanAsync(null, Ct);
        Assert.Equal("complete", first.Status);
        Assert.Equal(3, first.Metrics!.NewPosts);
        Assert.Equal(2, first.Metrics.Candidates);
        Assert.Equal(2, (await scanner.QueueAsync(false, Ct)).Single().Count);
        Assert.Equal("opening_post", (await scanner.PostAsync(false, Thread, Ct)).Reason);

        var repeat = await scanner.ScanAsync(null, Ct);
        Assert.Equal(3, repeat.Metrics!.RepeatedPosts);
        Assert.Equal(0, repeat.Metrics.ChangedPosts);
        await using var db = database.Open();
        Assert.Equal(3, await db.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<ContactObservation>().CountAsync(Ct));
        Assert.Empty(await db.Set<Analysis>().ToArrayAsync(Ct));
        Assert.Empty(await db.Set<Profile>().ToArrayAsync(Ct));

        var initial = await RevisionAsync(db, "tg @fixture_first_zq");
        var store = new ProductStore(db);
        var lease = await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(initial, lease!.RevisionId);

        body = Payload("Сообщение изменено и контакта больше нет.", "Email: Another@EXAMPLE.INVALID", "Discord: fixture_added_zq");
        var update = await scanner.ScanAsync(null, Ct);
        Assert.Equal(1, update.Metrics!.ChangedPosts);
        Assert.Equal(1, update.Metrics.NewPosts);
        Assert.Equal(2, (await scanner.PostAsync(false, Thread + 1, Ct)).Revisions);
        Assert.Equal("no_own_contact", (await scanner.PostAsync(false, Thread + 1, Ct)).Reason);
        var stale = Assert.Single(await scanner.QueueAsync(false, Ct), row => !row.Current);
        Assert.Equal("running", stale.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AnalyzeAsync(Analysis(initial), Ct));
        await store.CompleteAsync(lease, null, Ct);
        Assert.Equal("succeeded", Assert.Single(await scanner.QueueAsync(false, Ct), row => !row.Current).State);

        var active = await RevisionAsync(db, "Email: Another@EXAMPLE.INVALID");
        var profile = await store.AnalyzeAsync(Analysis(active), Ct);
        await store.ArchiveAsync(profile, "оператор", "синтетический архив", Ct);
        body = Payload("tg @fixture_first_zq", "Email: Edited@example.invalid");
        var reverted = await scanner.ScanAsync(null, Ct);
        Assert.Equal(2, reverted.Metrics!.ChangedPosts);
        Assert.Equal(1, reverted.Metrics.Excluded);
        var archived = await scanner.PostAsync(false, Thread + 2, Ct);
        Assert.Equal("moderation_excluded", archived.Reason);
        Assert.Equal(2, archived.Revisions);
        Assert.Null(archived.JobState);
        Assert.Equal("succeeded", (await scanner.PostAsync(false, Thread + 1, Ct)).JobState);

        var observation = await (from obs in db.Set<ContactObservation>()
                                 join contact in db.Set<Contact>() on obs.ContactId equals contact.Id
                                 where obs.RevisionId == initial
                                 select new { obs.RawValue, obs.Provenance, contact.NormalizedValue }).SingleAsync(Ct);
        Assert.Equal("@fixture_first_zq", observation.RawValue);
        Assert.Equal("fixture_first_zq", observation.NormalizedValue);
        Assert.StartsWith("dvach:body:", observation.Provenance, StringComparison.Ordinal);

        var next = await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(await RevisionAsync(db, "Discord: fixture_added_zq"), next!.RevisionId);
        Assert.Null(await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
        Assert.Equal(6, await db.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(3, await db.Set<ProcessingJob>().CountAsync(Ct));
    }

    [Fact]
    public async Task ReobservingLegacyDecisionsRefreshesContactsWithoutResettingJobsOrModeration()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var scanStore = new DvachScanStore(db);
        var state = await scanStore.LoadAsync(Key, Key, Ct);
        var thread = new DvachThread("soc", Thread);
        const string first = """<a class="post-reply-link" href="/soc/res/100000.html#99999" data-thread="100000" data-num="99999">&gt;&gt;99999</a> @sampleuser42""";
        const string partial = "Тг: @firstuser42 и @seconduser42";
        const string active = "Discord: Sample.Name";
        const string excluded = "@excludeduser42";

        async Task Seed(long number, string html, bool oldCandidate)
        {
            var prepared = DvachFilter.Prepare(thread, new DvachPost(number, Thread, html, null), Key);
            var contacts = oldCandidate ? prepared.Contacts.Take(1).ToArray() : [];
            await scanStore.ApplyAsync(state, prepared with
            {
                Reason = oldCandidate ? "candidate" : number == Thread + 1 ? "reply_to_other_post" : "no_own_contact",
                Contacts = contacts,
                Ambiguous = false,
            }, Ct);
        }

        await Seed(Thread + 1, first, false);
        await Seed(Thread + 2, partial, true);
        var product = new ProductStore(db);
        var completed = await product.ClaimAsync(TimeSpan.FromMinutes(10), Ct);
        Assert.Equal(await RevisionAsync(db, partial), completed!.RevisionId);
        await product.CompleteAsync(completed, null, Ct);
        await Seed(Thread + 3, active, true);
        var lease = await product.ClaimAsync(TimeSpan.FromMinutes(10), Ct);
        Assert.Equal(await RevisionAsync(db, active), lease!.RevisionId);
        await Seed(Thread + 4, excluded, false);
        var archived = await db.Set<OriginalPost>().SingleAsync(post => post.ExternalId == (Thread + 4).ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        archived.Excluded = true;
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        using var handler = new ThreadHandler((_, _) => Task.FromResult(Response(Payload(first, partial, active, excluded))));
        await using var provider = CreateService(database, handler, TimeProvider.System);
        var scanner = provider.GetRequiredService<DvachIngestion>();
        var scan = await scanner.ScanAsync(null, Ct);
        Assert.Equal("complete", scan.Status);
        Assert.Equal(1, scan.Metrics!.OtherReplies);
        var replied = await scanner.PostAsync(false, Thread + 1, Ct);
        Assert.Equal("candidate", replied.Reason);
        Assert.Equal("other_reply", replied.Reply);
        var updated = await scanner.PostAsync(false, Thread + 2, Ct);
        Assert.Equal(2, updated.Contacts);
        Assert.True(updated.Ambiguous);
        Assert.Equal("succeeded", updated.JobState);
        Assert.Equal("running", (await scanner.PostAsync(false, Thread + 3, Ct)).JobState);
        Assert.Equal("moderation_excluded", (await scanner.PostAsync(false, Thread + 4, Ct)).Reason);
        Assert.Null((await scanner.PostAsync(false, Thread + 4, Ct)).JobState);
        Assert.Equal(5, await db.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(5, await db.Set<ContactObservation>().CountAsync(Ct));
        Assert.Equal(3, await db.Set<ProcessingJob>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<JobAttempt>().CountAsync(Ct));

        Assert.Equal("complete", (await scanner.ScanAsync(null, Ct)).Status);
        Assert.Equal(5, await db.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(5, await db.Set<ContactObservation>().CountAsync(Ct));
        Assert.Equal(3, await db.Set<ProcessingJob>().CountAsync(Ct));
        await product.CompleteAsync(lease, null, Ct);
        Assert.Equal("succeeded", (await scanner.PostAsync(false, Thread + 3, Ct)).JobState);
    }

    [Fact]
    public async Task ResumingOldPendingSnapshotUsesCurrentFilterWithoutHttp()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var db = database.Open())
        {
            var store = new DvachScanStore(db);
            var state = await store.LoadAsync(Key, Key, Ct);
            var thread = new DvachThread("soc", Thread);
            var original = DvachFilter.Prepare(thread, new DvachPost(Thread + 1, Thread, "@sampleuser42", null), Key);
            var old = original with { Reason = "no_own_contact", Contacts = [] };
            state.Pending = JsonSerializer.Serialize(new[] { old });
            state.Metrics = JsonSerializer.Serialize(new DvachScanMetrics(1));
            state.Applied = 0;
            state.Status = "interrupted";
            await store.SaveAsync(Ct);
        }

        using var offline = new ThreadHandler((_, _) => throw new InvalidOperationException("Снимок уже сохранён."));
        await using var provider = CreateService(database, offline, TimeProvider.System);
        var scanner = provider.GetRequiredService<DvachIngestion>();
        Assert.Equal("complete", (await scanner.ScanAsync(null, Ct)).Status);
        var post = await scanner.PostAsync(false, Thread + 1, Ct);
        Assert.Equal("candidate", post.Reason);
        Assert.Equal(1, post.Contacts);
        Assert.Equal("pending", post.JobState);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("contact")]
    [InlineData("queue")]
    public async Task PendingSnapshotSurvivesFailureAndRestartsWithoutHttp(string failure)
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var admin = database.Open(runtime: false);
        var (table, previousRows, timing) = failure switch
        {
            "revision" => ("PostRevision", 2, "AFTER"),
            "contact" => ("ContactObservation", 1, "AFTER"),
            _ => ("ProcessingJob", 1, "BEFORE"),
        };
        // Только собственная тестовая БД. Триггер заставляет упасть третье сообщение снимка.
        var definition = $$"""
            CREATE FUNCTION product.test_source_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF (SELECT count(*) FROM product."{{table}}") >= {{previousRows}} + (CASE WHEN TG_WHEN='AFTER' THEN 1 ELSE 0 END) THEN
                    RAISE EXCEPTION 'Синтетический отказ записи';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_source_failure {{timing}} INSERT ON product."{{table}}"
                FOR EACH ROW EXECUTE FUNCTION product.test_source_failure();
            """;
        await using (var install = new NpgsqlCommand(definition, (NpgsqlConnection)admin.Database.GetDbConnection()))
        {
            await admin.Database.OpenConnectionAsync(Ct);
            await install.ExecuteNonQueryAsync(Ct);
        }

        var clock = new TestClock();
        using var handler = new ThreadHandler((_, _) => Task.FromResult(Response(Payload("tg @fixture_first_zq", "tg @fixture_second_zq"))));
        await using (var provider = CreateService(database, handler, clock))
        {
            var ingestion = provider.GetRequiredService<DvachIngestion>();
            var stopped = await ingestion.ScanAsync(null, Ct);
            Assert.Equal("failed", stopped.Status);
            Assert.Equal("apply", stopped.Phase);
            Assert.Equal("database_unavailable", stopped.Failure);
            Assert.True(stopped.HasPendingSnapshot);
            Assert.Null(stopped.LastSuccessAt);
            Assert.Equal(2, stopped.Applied);
            Assert.Equal("backoff", (await ingestion.ScanAsync(null, Ct)).Status);
        }

        await using var runtime = database.Open();
        Assert.Equal(2, await runtime.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(1, await runtime.Set<ContactObservation>().CountAsync(Ct));
        Assert.Equal(1, await runtime.Set<ProcessingJob>().CountAsync(Ct));
        await using (var remove = new NpgsqlCommand($"DROP TRIGGER test_source_failure ON product.\"{table}\"",
            (NpgsqlConnection)admin.Database.GetDbConnection()))
        {
            await remove.ExecuteNonQueryAsync(Ct);
        }

        clock.Advance(TimeSpan.FromMinutes(6));
        using var offline = new ThreadHandler((_, _) => throw new InvalidOperationException("Снимок уже сохранён."));
        await using var restarted = CreateService(database, offline, clock);
        var resumed = await restarted.GetRequiredService<DvachIngestion>().ScanAsync(null, Ct);
        Assert.Equal("complete", resumed.Status);
        Assert.False(resumed.HasPendingSnapshot);
        Assert.NotNull(resumed.LastSuccessAt);
        Assert.Equal(3, resumed.Applied);
        Assert.Equal(3, resumed.Metrics!.NewPosts);
        Assert.Equal(3, await runtime.Set<PostRevision>().CountAsync(Ct));
        Assert.Equal(2, await runtime.Set<ContactObservation>().CountAsync(Ct));
        Assert.Equal(2, await runtime.Set<ProcessingJob>().CountAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentScanAndUnreliableResponseDoNotAdvanceSuccess()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ThreadHandler(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(Payload("tg @fixture_first_zq"));
        });
        await using var provider = CreateService(database, handler, TimeProvider.System);
        var ingestion = provider.GetRequiredService<DvachIngestion>();
        var scan = ingestion.ScanAsync(null, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal("busy", (await ingestion.ScanAsync(null, Ct)).Status);
            Assert.Equal("running", (await ingestion.StatusAsync(false, Ct)).Status);
        }
        catch
        {
            // Опрос обязан завершиться до удаления тестовой БД; отказ проверки
            // остаётся исходной ошибкой теста.
            release.TrySetResult();
            await DrainAsync(scan);
            throw;
        }

        release.TrySetResult();
        var success = await scan;
        Assert.Equal("complete", success.Status);
        using var unavailable = new ThreadHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
            return Task.FromResult(response);
        });
        await using var nextProvider = CreateService(database, unavailable, TimeProvider.System);
        var next = nextProvider.GetRequiredService<DvachIngestion>();
        var rateLimited = await next.ScanAsync(null, Ct);
        Assert.Equal("rate_limited", rateLimited.Failure);
        Assert.Equal(429, rateLimited.HttpStatus);
        Assert.True(rateLimited.RetryAt > DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Equal(success.LastSuccessAt!.Value.ToUnixTimeMilliseconds(), rateLimited.LastSuccessAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal("backoff", (await next.ScanAsync(null, Ct)).Status);
        Assert.Equal(1, (await next.QueueAsync(false, Ct)).Single().Count);
    }

    [Fact]
    public async Task PartialSnapshotAndCancelledFetchKeepPreviousSuccessAndQueue()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        var clock = new TestClock();
        var valid = Payload("tg @fixture_first_zq");
        var response = valid;
        using var handler = new ThreadHandler((_, _) => Task.FromResult(Response(response)));
        await using var provider = CreateService(database, handler, clock);
        var ingestion = provider.GetRequiredService<DvachIngestion>();
        var complete = await ingestion.ScanAsync(null, Ct);
        var lastSuccess = complete.LastSuccessAt!.Value.ToUnixTimeMilliseconds();

        response = Payload().Replace("\"posts_count\":1", "\"posts_count\":2", StringComparison.Ordinal);
        var partial = await ingestion.ScanAsync(null, Ct);
        Assert.Equal("incomplete_snapshot", partial.Failure);
        Assert.Equal(0, partial.Applied);
        Assert.False(partial.HasPendingSnapshot);
        Assert.Equal(lastSuccess, partial.LastSuccessAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(1, (await ingestion.QueueAsync(false, Ct)).Single().Count);

        clock.Advance(TimeSpan.FromMinutes(6));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var blocked = new ThreadHandler((_, ct) =>
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Response(valid));
        });
        await using var nextProvider = CreateService(database, blocked, clock);
        var next = nextProvider.GetRequiredService<DvachIngestion>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.ScanAsync(null, cancel.Token));
        var interrupted = await next.StatusAsync(false, Ct);
        Assert.Equal("interrupted", interrupted.Status);
        Assert.Equal("cancelled", interrupted.Failure);
        Assert.Equal(lastSuccess, interrupted.LastSuccessAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal("candidate", (await next.PostAsync(false, Thread + 1, Ct)).Reason);
    }

    private static void RequireDocker()
    {
        if (!PostgresTestCluster.Enabled)
            Assert.Skip("Требуется ANKETAPARSER_DOCKER_TESTS=1.");
    }

    /// <summary>Дождаться завершения опроса, не скрывая исходную ошибку проверки.</summary>
    private static async Task DrainAsync(Task scan)
    {
        try
        {
            await scan.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (Exception)
        {
            // Отказ опроса не заменяет ошибку проверки, из-за которой тест уже падает.
        }
    }

    private static ServiceProvider CreateService(TestDatabase database, HttpMessageHandler handler, TimeProvider clock)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = "product_runtime",
            Timeout = 3,
            CommandTimeout = 5,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dvach:ThreadUrl"] = Key,
            ["Database:ConnectionString"] = connection.ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton<HttpClient>(_ => new HttpClient(handler, disposeHandler: false));
        services.AddAnketaParserInfrastructure(config, "Test", []);
        return services.BuildServiceProvider();
    }

    private static string Payload(params string[] comments)
    {
        var posts = comments.Prepend("Начальный пост").Select((comment, i) => new
        {
            board = "soc",
            num = Thread + i,
            number = i + 1,
            parent = i == 0 ? 0 : Thread,
            comment,
        });
        return JsonSerializer.Serialize(new
        {
            board = new { id = "soc" },
            current_thread = Thread,
            is_board = false,
            is_index = false,
            is_closed = 0,
            max_num = Thread + comments.Length,
            posts_count = comments.Length + 1,
            threads = new[] { new { posts } },
        });
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static Task<Guid> RevisionAsync(ProductDbContext db, string body) =>
        db.Set<PostRevision>().Where(x => x.Content == body).Select(x => x.Id).SingleAsync(Ct);

    private static AnalysisInput Analysis(Guid id) =>
        new(id, "test", "test", null, null, null, null, null, "{}", "{}");

    private sealed class ThreadHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan by) => _now += by;
    }
}