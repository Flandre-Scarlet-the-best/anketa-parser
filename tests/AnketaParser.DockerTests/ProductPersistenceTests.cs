using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace AnketaParser.DockerTests;

public sealed class ProductPersistenceTests(PostgresTestCluster cluster) : IClassFixture<PostgresTestCluster>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentImportPreservesRevisionsAndUnknownFields()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        var input = new PostInput("https://example.invalid/thread", "1", "https://example.invalid/1", "Синтетическая анкета", null);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = database.Open();
            return await new ProductStore(db).ImportAsync(input, Ct);
        }));
        Assert.Single(ids.Distinct());
        await using var context = database.Open();
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
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
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
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
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
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var id = await store.EnqueueAsync(revision, "synthetic-v1", 2, Ct);
        Assert.Equal(id, await store.EnqueueAsync(revision, "synthetic-v1", 2, Ct));
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var worker = database.Open();
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
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = await database.CreateConflictDatabaseAsync(Ct);
        await Assert.ThrowsAsync<PostgresException>(() => SchemaMaintenance.MigrateAsync(db, Ct));
        Assert.Equal("migration_required", await SchemaMaintenance.StatusAsync(db, Ct));
        Assert.Equal(42, (await db.Database.SqlQueryRaw<int>("""SELECT marker AS "Value" FROM product."Source" """).ToListAsync(Ct)).Single());
    }

    [Fact]
    public async Task MigrationPreservesExistingDataAndRuntimeCannotEscalate()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var admin = database.Open(runtime: false);
        await SchemaMaintenance.MigrateAsync(admin, Ct);
        await using var db = database.Open(runtime: true);
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

    private static void RequireDocker()
    {
        if (!PostgresTestCluster.Enabled)
        {
            Assert.Skip("Требуется ANKETAPARSER_DOCKER_TESTS=1.");
        }
    }

    private static AnalysisInput Analysis(Guid revision) => new(revision, "synthetic", "v1", null, null, null, null, null, "{}", "{}");
    private static Task<Guid> Import(ProductStore store, string id) => store.ImportAsync(new("https://example.invalid/thread", id, "https://example.invalid/" + id, "Синтетический пост " + id, null), Ct);
    private static Task<Guid> Contact(ProductStore store, Guid revision, string platform, string value) => store.RegisterContactAsync(new(revision, platform, value, value, 0.9m, "синтетическое извлечение"), Ct);
}