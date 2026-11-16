using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace AnketaParser.DockerTests;

public sealed class ProductPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentImportPreservesRevisionsAndUnknownFields()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        var input = new PostInput("https://example.invalid/thread", "1", "https://example.invalid/1", "Синтетическая анкета", null);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = fixture.Open();
            return await new ProductStore(db).ImportAsync(input, Ct);
        }));
        Assert.Single(ids.Distinct());
        await using var context = fixture.Open();
        var store = new ProductStore(context);
        var updated = await store.ImportAsync(input with { Content = "Изменённая синтетическая анкета" }, Ct);
        Assert.NotEqual(ids[0], updated);
        Assert.Equal(1, await context.Set<OriginalPost>().CountAsync(Ct));
        Assert.Equal(2, await context.Set<PostRevision>().CountAsync(Ct));
        var profile = await store.AnalyzeAsync(Analysis(ids[0]), Ct);
        await store.PublishAsync(profile, Ct);
        var row = await context.Set<Analysis>().SingleAsync(Ct);
        Assert.Null(row.Age); Assert.Null(row.Location); Assert.Null(row.GenderDescription);
        Assert.Equal("{}", row.Observations);
    }

    [Fact]
    public async Task ContactsPreserveAmbiguityAndUsernameCanChangeOwner()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        await using var db = fixture.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var telegram = await Contact(store, revision, "telegram", "first");
        var other = await Contact(store, revision, "telegram", "second");
        var email = await Contact(store, revision, "email", "test@example.invalid");
        Assert.Equal(3, await db.Set<Contact>().CountAsync(Ct));
        var account = await store.ConfirmTelegramAsync(telegram, 10001, "shared", "синтетическая проверка", Ct);
        await store.ConfirmTelegramAsync(telegram, 10001, "changed", "смена username", Ct);
        var second = await store.ConfirmTelegramAsync(other, 10002, "shared", "новый владелец username", Ct);
        Assert.NotEqual(account, second);
        Assert.Equal(3, await db.Set<TelegramUsername>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<TelegramUsername>().CountAsync(x => x.ValidTo == null, Ct));
        var anon = await store.LinkAsync(profile, telegram, Guid.Empty, LinkConfidence.VerifiedAccount, "проверен аккаунт, авторство неизвестно", Ct);
        await store.LinkAsync(profile, other, anon, LinkConfidence.Suggested, "неоднозначность", Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.LinkAsync(profile, email, anon, LinkConfidence.VerifiedAccount, "нельзя подтвердить по email", Ct));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Set<Anon>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<ProfileLink>().CountAsync(Ct));
        await Assert.ThrowsAsync<DbUpdateException>(() => store.LinkAsync(profile, other, anon, LinkConfidence.VerifiedAccount, "разные проверенные аккаунты", Ct));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmTelegramAsync(email, 10003, null, "неверная платформа", Ct));
    }

    [Fact]
    public async Task ArchiveIsTerminalAndComplaintsAndBlacklistsKeepOwners()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        await using var db = fixture.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var contact = await Contact(store, revision, "discord", "synthetic");
        var anon = await store.LinkAsync(profile, contact, Guid.Empty, LinkConfidence.Suggested, "публичное упоминание", Ct);
        await store.PublishAsync(profile, Ct);
        var nextRevision = await Import(store, "2");
        var next = await store.AnalyzeAsync(Analysis(nextRevision), Ct);
        var nextContact = await Contact(store, nextRevision, "discord", "synthetic");
        Assert.Equal(contact, nextContact);
        await store.LinkAsync(next, contact, anon, LinkConfidence.Suggested, "предполагаемая связь", Ct);
        await store.PublishAsync(next, Ct);
        var user1 = await store.RegisterBotUserAsync(20001, Ct);
        var user2 = await store.RegisterBotUserAsync(20002, Ct);
        await store.BlacklistAsync(user1, anon, "личная причина", Ct);
        await store.BlacklistAsync(user1, anon, "уточнение", Ct);
        var complaint = await store.ComplainAsync(user1, next, contact, "чужой контакт", Ct);
        await store.RecordOwnershipCheckAsync(complaint, OwnershipCheckResult.Unknown, "администратор", "недостаточно данных", Ct);
        await store.ComplainAsync(user2, next, contact, "повторная жалоба", Ct);
        await store.SaveSettingsAsync(user1, new Dictionary<string, string> { ["language"] = "ru" }, Ct);
        Assert.Equal("ru", (await store.ReadSettingsAsync(user1, Ct))["language"]);
        Assert.Empty(await store.ReadSettingsAsync(user2, Ct));
        Assert.Single(await store.ReadBlacklistAsync(user1, Ct));
        Assert.Empty(await store.ReadBlacklistAsync(user2, Ct));
        Assert.Equal(next, (await store.LatestAsync(anon, Ct))!.Id);
        Assert.Equal(1, await db.Set<BlacklistEntry>().CountAsync(x => x.OwnerId == user1, Ct));
        Assert.Equal(0, await db.Set<BlacklistEntry>().CountAsync(x => x.OwnerId == user2, Ct));
        Assert.Equal(2, await db.Set<Complaint>().Select(x => x.OwnerId).Distinct().CountAsync(Ct));
        await store.ArchiveAsync(next, "администратор", "подтверждённая подделка", Ct);
        Assert.Equal(profile, (await store.LatestAsync(anon, Ct))!.Id);
        Assert.Equal(nextRevision, await Import(store, "2"));
        await Assert.ThrowsAsync<DbUpdateException>(() => store.PublishAsync(next, Ct));
        db.ChangeTracker.Clear();
        var reprocessed = await store.AnalyzeAsync(Analysis(nextRevision), Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.PublishAsync(reprocessed, Ct));
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Set<ModerationDecision>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<Anon>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<OriginalPost>().CountAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentJobClaimsUseFencingAndBoundedRetries()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        await using var db = fixture.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var id = await store.EnqueueAsync(revision, "synthetic-v1", 2, Ct);
        Assert.Equal(id, await store.EnqueueAsync(revision, "synthetic-v1", 2, Ct));
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var worker = fixture.Open();
            return await new ProductStore(worker).ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        }));
        var first = Assert.Single(leases.OfType<JobLease>());
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE product."ProcessingJob" SET "LeaseUntil"={DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE "Id"={id}""", Ct);
        var second = (await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct))!;
        Assert.NotEqual(first.Token, second.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(first, null, Ct));
        await store.CompleteAsync(second, "llm_unavailable", Ct);
        Assert.Null(await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
        db.ChangeTracker.Clear();
        Assert.Equal("failed", (await db.Set<ProcessingJob>().SingleAsync(Ct)).State);
        Assert.Equal(2, await db.Set<JobAttempt>().CountAsync(Ct));
        Assert.All(await db.Set<JobAttempt>().ToArrayAsync(Ct), x => Assert.NotNull(x.FinishedAt));
        Assert.NotEqual(Guid.Empty, await Import(store, "2"));
    }

    [Fact]
    public async Task FailedMigrationCannotAdvertiseReadyOrOverwriteExistingTable()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        await using var db = await fixture.CreateConflictDatabaseAsync(Ct);
        await Assert.ThrowsAsync<PostgresException>(() => SchemaMaintenance.MigrateAsync(db, Ct));
        Assert.Equal("migration_required", await SchemaMaintenance.StatusAsync(db, Ct));
        Assert.Equal(42, (await db.Database.SqlQueryRaw<int>("""SELECT marker AS "Value" FROM product."Source" """).ToListAsync(Ct)).Single());
    }

    [Fact]
    public async Task MigrationPreservesExistingDataAndRuntimeCannotEscalate()
    {
        await using var fixture = await DatabaseFixture.StartAsync(Ct);
        await using var admin = fixture.Open(runtime: false);
        await SchemaMaintenance.MigrateAsync(admin, Ct);
        await using var db = fixture.Open(runtime: true);
        var store = new ProductStore(db);
        await Import(store, "1");
        Assert.Equal("ready", await SchemaMaintenance.StatusAsync(db, Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE product.forbidden(id int)", Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE forbidden(id int)", Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("ALTER ROLE product_runtime SUPERUSER", Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM pg_authid", Ct));
        Assert.Equal(1, await db.Set<OriginalPost>().CountAsync(Ct));
        await admin.Database.ExecuteSqlRawAsync("""INSERT INTO public."__EFMigrationsHistory" VALUES ('99999999999999_unknown','10.0.14')""", Ct);
        Assert.Equal("newer_or_unknown", await SchemaMaintenance.StatusAsync(db, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMaintenance.MigrateAsync(admin, Ct));
    }

    private static AnalysisInput Analysis(Guid revision) => new(revision, "synthetic", "v1", null, null, null, null, null, "{}", "{}");
    private static Task<Guid> Import(ProductStore store, string id) => store.ImportAsync(new("https://example.invalid/thread", id, "https://example.invalid/" + id, "Синтетический пост " + id, null), Ct);
    private static Task<Guid> Contact(ProductStore store, Guid revision, string platform, string value) => store.RegisterContactAsync(new(revision, platform, value, value, 0.9m, "синтетическое извлечение"), Ct);

    private sealed class DatabaseFixture(DockerCli docker, string name, string connectionString) : IAsyncDisposable
    {
        public async Task<ProductDbContext> CreateConflictDatabaseAsync(CancellationToken ct)
        {
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand("CREATE DATABASE conflict_test OWNER product_migrator", admin);
            await create.ExecuteNonQueryAsync(ct);
            var cs = new NpgsqlConnectionStringBuilder(connectionString) { Database = "conflict_test", Username = "product_migrator" };
            var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(cs.ConnectionString).Options);
            await db.Database.ExecuteSqlRawAsync("""CREATE SCHEMA product; CREATE TABLE product."Source"(marker int); INSERT INTO product."Source" VALUES(42)""", ct);
            return db;
        }

        public ProductDbContext Open(bool runtime = true)
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString);
            cs.Username = runtime ? "product_runtime" : "product_migrator";
            return new(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(cs.ConnectionString).Options);
        }

        public static async Task<DatabaseFixture> StartAsync(CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("ANKETAPARSER_DOCKER_TESTS") != "1") Assert.Skip("Требуется ANKETAPARSER_DOCKER_TESTS=1.");
            var root = ComposeStackSmokeTests.FindRepositoryRoot();
            var docker = new DockerCli(root);
            var name = "anketa-persistence-test-" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            await docker.RunAsync(["run", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=" + password, "-p", "127.0.0.1::5432", "postgres:18.6-alpine"], TimeSpan.FromMinutes(3), ct);
            var port = await docker.RunAsync(["port", name, "5432"], TimeSpan.FromSeconds(10), ct);
            var cs = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = int.Parse(port.Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture), Database = "postgres", Username = "postgres", Password = password };
            var fixture = new DatabaseFixture(docker, name, cs.ConnectionString);
            try
            {
                for (var i = 0; ; i++)
                {
                    try
                    {
                        await using var connection = new NpgsqlConnection(cs.ConnectionString);
                        await connection.OpenAsync(ct);
                        await using var marker = new NpgsqlCommand("CREATE TABLE public.existing_marker(id int); INSERT INTO public.existing_marker VALUES(42)", connection);
                        await marker.ExecuteNonQueryAsync(ct);
                        break;
                    }
                    catch (NpgsqlException) when (i < 60) { await Task.Delay(500, ct); }
                }
                await SchemaMaintenance.ProvisionAsync(cs.ConnectionString, password, password, password, ct);
                await SchemaMaintenance.ProvisionAsync(cs.ConnectionString, password, password, password, ct);
                await using var db = fixture.Open(runtime: false);
                Assert.Equal("migration_required", await SchemaMaintenance.StatusAsync(db, ct));
                await SchemaMaintenance.MigrateAsync(db, ct);
                await SchemaMaintenance.MigrateAsync(db, ct);
                await using var verify = new NpgsqlConnection(cs.ConnectionString);
                await verify.OpenAsync(ct);
                await using var query = new NpgsqlCommand("SELECT id FROM public.existing_marker", verify);
                Assert.Equal(42, await query.ExecuteScalarAsync(ct));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await docker.RunAsync(["rm", "-fv", name], TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }
}