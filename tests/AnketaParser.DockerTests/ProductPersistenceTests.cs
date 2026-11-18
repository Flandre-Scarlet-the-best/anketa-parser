using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace AnketaParser.DockerTests;

public sealed class ProductPersistenceTests(PostgresTestCluster cluster) : IClassFixture<PostgresTestCluster>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TypedLinksAreAddressableAndRepeatedUnlinkPreservesHistory()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        using var services = new ServiceCollection().AddScoped<IProductStore>(_ => new ProductStore(db)).BuildServiceProvider();
        var store = services.GetRequiredService<IProductStore>();
        var revision = await store.ImportAsync(new("synthetic", "1", "https://example.invalid/1", "Синтетический пост", null), Ct);
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var contact = await store.RegisterContactAsync(new(revision, "discord", "example", "example", 0.5m, "упоминание"), Ct);
        var first = await store.LinkAsync(profile, contact, Guid.Empty, LinkConfidence.Suggested, "неподтверждённая связь", Ct);
        var second = await store.LinkAsync(profile, contact, first.AnonId, LinkConfidence.Suggested, "другое основание", Ct);
        Assert.NotEqual(first.LinkId, second.LinkId);
        Assert.Equal(first.AnonId, second.AnonId);
        await store.PublishAsync(profile, Ct);
        await store.UnlinkAsync(first.LinkId, "первоначальная причина", Ct);
        var original = await db.Set<ProfileLink>().AsNoTracking().SingleAsync(x => x.Id == first.LinkId, Ct);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var concurrent = database.Open();
            await new ProductStore(concurrent).UnlinkAsync(first.LinkId, "повторная причина", Ct);
        }));
        var unchanged = await db.Set<ProfileLink>().AsNoTracking().SingleAsync(x => x.Id == first.LinkId, Ct);
        Assert.NotNull(original.UnlinkedAt);
        Assert.Equal(original.UnlinkedAt, unchanged.UnlinkedAt);
        Assert.Equal("первоначальная причина", unchanged.UnlinkReason);
        Assert.Equal("неподтверждённая связь", unchanged.Provenance);
        Assert.Null((await db.Set<ProfileLink>().AsNoTracking().SingleAsync(x => x.Id == second.LinkId, Ct)).UnlinkedAt);
        Assert.Equal(profile, (await store.LatestAsync(first.AnonId, Ct))!.Id);
        await store.UnlinkAsync(second.LinkId, "отмена оставшейся связи", Ct);
        Assert.Null(await store.LatestAsync(first.AnonId, Ct));
        Assert.Equal(1, await db.Set<Anon>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<Profile>().CountAsync(Ct));
    }

    [Fact]
    public async Task TypedReviewAndRejectionRequireExplicitReconsiderationAndPreserveExclusion()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        using var services = new ServiceCollection().AddScoped<IProductStore>(_ => new ProductStore(db)).BuildServiceProvider();
        var store = services.GetRequiredService<IProductStore>();
        var input = new PostInput("synthetic", "1", "https://example.invalid/1", "Синтетический пост", null);
        var revision = await store.ImportAsync(input, Ct);
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var contact = await store.RegisterContactAsync(new(revision, "email", "x@example.invalid", "x@example.invalid", 0.3m, "упоминание"), Ct);
        var link = await store.LinkAsync(profile, contact, Guid.Empty, LinkConfidence.Suggested, "совпадение не доказывает личность", Ct);
        Assert.Equal(ProfileState.Candidate, (await db.Set<Profile>().AsNoTracking().SingleAsync(Ct)).State);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReviewAsync(profile, "", "основание", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RejectAsync(profile, "оператор", "", Ct));
        await store.ReviewAsync(profile, "оператор", "проверено вручную", Ct);
        Assert.Equal(ProfileState.Reviewed, (await db.Set<Profile>().AsNoTracking().SingleAsync(Ct)).State);
        Assert.Null(await store.LatestAsync(link.AnonId, Ct));
        await store.PublishAsync(profile, Ct);
        await store.RejectAsync(profile, "оператор", "неверные сведения", Ct);
        await store.RejectAsync(profile, "другой оператор", "повтор", Ct);
        Assert.Equal(ProfileState.Rejected, (await db.Set<Profile>().AsNoTracking().SingleAsync(Ct)).State);
        Assert.Null(await store.LatestAsync(link.AnonId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(profile, Ct));
        await store.ReviewAsync(profile, "оператор", "повторная проверка", Ct);
        await store.PublishAsync(profile, Ct);
        var user = await store.RegisterBotUserAsync(30001, Ct);
        await store.ComplainAsync(user, profile, contact, "сомнение", Ct);
        Assert.Equal(profile, (await store.LatestAsync(link.AnonId, Ct))!.Id);
        await store.ArchiveAsync(profile, "оператор", "подтверждённая подделка", Ct);
        Assert.Null(await store.LatestAsync(link.AnonId, Ct));
        Assert.Equal(revision, await store.ImportAsync(input, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewAsync(profile, "оператор", "обход архива", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RejectAsync(profile, "оператор", "обход архива", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(profile, Ct));
        db.ChangeTracker.Clear();
        var history = await db.Set<ModerationDecision>().OrderBy(x => x.CreatedAt).ToArrayAsync(Ct);
        Assert.Equal(["review", "reject", "review", "archive"], history.Select(x => x.Action));
        Assert.Equal(["проверено вручную", "неверные сведения", "повторная проверка", "подтверждённая подделка"], history.Select(x => x.Reason));
        Assert.All(history, x => Assert.Equal("оператор", x.Actor));
    }

    [Fact]
    public async Task ExpiredFinalLeaseClosesAttemptAndRejectsStaleCompletion()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var id = await store.EnqueueAsync(revision, "synthetic-final", 1, Ct);
        var lease = (await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct))!;
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE product."ProcessingJob" SET "LeaseUntil"={DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE "Id"={id}""", Ct);
        Assert.Null(await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
        db.ChangeTracker.Clear();
        var job = await db.Set<ProcessingJob>().SingleAsync(Ct);
        Assert.Equal("failed", job.State);
        Assert.Null(job.LeaseToken);
        Assert.Null(job.LeaseUntil);
        var attempt = await db.Set<JobAttempt>().SingleAsync(Ct);
        Assert.NotNull(attempt.FinishedAt);
        Assert.Equal("lease_expired", attempt.FailureCategory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(lease, null, Ct));
    }

    [Fact]
    public async Task UsedVerificationIdentityCannotChangeButNewEvidenceCanBeRecorded()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var first = await Contact(store, revision, "telegram", "first");
        var second = await Contact(store, revision, "telegram", "second");
        await store.ConfirmTelegramAsync(first, 10001, null, "проверка первого контакта", Ct);
        await store.ConfirmTelegramAsync(second, 10001, null, "проверка второго контакта", Ct);
        var anon = (await store.LinkAsync(profile, first, Guid.Empty, LinkConfidence.VerifiedAccount, "проверка", Ct)).AnonId;
        await store.LinkAsync(profile, second, anon, LinkConfidence.VerifiedAccount, "тот же аккаунт", Ct);
        var otherAccount = await store.ConfirmTelegramAsync(first, 10002, null, "новое независимое подтверждение", Ct);
        var used = await db.Set<ContactVerification>().SingleAsync(x => x.ContactId == second, Ct);
        var originalAccount = used.AccountId;
        var accountError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."ContactVerification" SET "AccountId"={otherAccount} WHERE "Id"={used.Id}""", Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, accountError.SqlState);
        var contactError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."ContactVerification" SET "ContactId"={first} WHERE "Id"={used.Id}""", Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, contactError.SqlState);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE product."ContactVerification" SET "Provenance"='уточнённое доказательство' WHERE "Id"={used.Id}""", Ct);
        db.ChangeTracker.Clear();
        var unchanged = await db.Set<ContactVerification>().SingleAsync(x => x.Id == used.Id, Ct);
        Assert.Equal(originalAccount, unchanged.AccountId);
        Assert.Equal(second, unchanged.ContactId);
        Assert.Equal("уточнённое доказательство", unchanged.Provenance);
    }

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
        var anon = (await store.LinkAsync(profile, telegram, Guid.Empty, LinkConfidence.VerifiedAccount, "проверен аккаунт, авторство неизвестно", Ct)).AnonId;
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
        var anon = (await store.LinkAsync(profile, contact, Guid.Empty, LinkConfidence.Suggested, "публичное упоминание", Ct)).AnonId;
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(next, Ct));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AnalyzeAsync(Analysis(nextRevision), Ct));
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Set<ModerationDecision>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<Anon>().CountAsync(Ct));
        Assert.Equal(2, await db.Set<OriginalPost>().CountAsync(Ct));
    }

    [Fact]
    public async Task ProfileAnalysisCannotMovePastLinkedContactObservations()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        const string source = "https://example.invalid/thread";
        const string externalId = "profile-origin";
        var firstRevision = await store.ImportAsync(new(source, externalId, source + "/profile-origin", "Первая ревизия", null), Ct);
        var profile = await store.AnalyzeAsync(Analysis(firstRevision), Ct);
        var firstContact = await store.RegisterContactAsync(
            new(firstRevision, "discord", "first", "first-contact", 0.9m, "синтетическое наблюдение"), Ct);
        await store.LinkAsync(profile, firstContact, Guid.Empty, LinkConfidence.Suggested, "синтетическая связь", Ct);

        var secondRevision = await store.ImportAsync(new(source, externalId, source + "/profile-origin", "Вторая ревизия", null), Ct);
        var secondProfile = await store.AnalyzeAsync(Analysis(secondRevision), Ct);
        var secondAnalysis = await db.Set<Profile>().Where(x => x.Id == secondProfile).Select(x => x.AnalysisId).SingleAsync(Ct);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."Profile" SET "AnalysisId"={secondAnalysis} WHERE "Id"={profile}""", Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("profile_link_analysis_mismatch", exception.MessageText);
    }

    [Fact]
    public async Task SaveSettingsRewritesValueAfterAnotherContextChangesIt()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var firstContext = database.Open();
        await using var secondContext = database.Open();
        var firstStore = new ProductStore(firstContext);
        var secondStore = new ProductStore(secondContext);
        var owner = await firstStore.RegisterBotUserAsync(20003, Ct);
        var firstValue = new Dictionary<string, string> { ["mode"] = "first" };
        await firstStore.SaveSettingsAsync(owner, firstValue, Ct);
        await secondStore.SaveSettingsAsync(owner, new Dictionary<string, string> { ["mode"] = "second" }, Ct);

        await firstStore.SaveSettingsAsync(owner, firstValue, Ct);

        Assert.Equal("first", (await secondStore.ReadSettingsAsync(owner, Ct))["mode"]);
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
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."JobAttempt" SET "Token"={first.Token} WHERE "Token"={second.Token}""", Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("IX_JobAttempt_Token", duplicate.ConstraintName);
        Assert.NotEqual(Guid.Empty, await Import(store, "2"));
    }

    [Fact]
    public async Task ClaimSkipsExhaustedJobLockedByCompletionAndClaimsIndependentWork()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var setup = database.Open();
        var store = new ProductStore(setup);
        var expiredRevision = await Import(store, "expired-claim");
        var expiredJob = await store.EnqueueAsync(expiredRevision, "synthetic-v1", 1, Ct);
        var lease = await store.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.NotNull(lease);
        await setup.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."ProcessingJob" SET "LeaseUntil"={DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE "Id"={expiredJob}""",
            Ct);

        var availableRevision = await Import(store, "independent-claim");
        var availableJob = await store.EnqueueAsync(availableRevision, "synthetic-v1", 1, Ct);
        await using var locker = database.Open();
        await locker.Database.OpenConnectionAsync(Ct);
        await using var transaction = await locker.Database.BeginTransactionAsync(Ct);
        await locker.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."ProcessingJob" SET "FailureCategory"="FailureCategory" WHERE "Id"={expiredJob}""",
            Ct);

        await using var claimant = database.Open();
        await claimant.Database.OpenConnectionAsync(Ct);
        await claimant.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms'", Ct);
        var claimed = await new ProductStore(claimant).ClaimAsync(TimeSpan.FromMinutes(1), Ct);

        Assert.NotNull(claimed);
        Assert.Equal(availableJob, claimed.JobId);
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
        await admin.Database.ExecuteSqlRawAsync("""INSERT INTO product."__EFMigrationsHistory" VALUES ('99999999999999_unknown','10.0.14')""", Ct);
        Assert.Equal("newer_or_unknown", await SchemaMaintenance.StatusAsync(db, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMaintenance.MigrateAsync(admin, Ct));
    }

    [Fact]
    public async Task ProvisioningKeepsRestrictedRolesWorkingWithoutPublicSchemaUsage()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            // Существующая установка: USAGE на public мог быть отозван у PUBLIC, а прямой grant истории — тоже.
            await using var revoke = new NpgsqlCommand(
                """REVOKE USAGE ON SCHEMA public FROM PUBLIC; REVOKE SELECT ON TABLE product."__EFMigrationsHistory" FROM product_runtime, product_backup""",
                admin);
            await revoke.ExecuteNonQueryAsync(Ct);
        }

        await SchemaMaintenance.ProvisionAsync(
            database.ConnectionString,
            database.RolePassword,
            database.RolePassword,
            database.RolePassword,
            Ct);

        await using (var runtime = database.Open())
        {
            // Проверка версии схемы выполняется фактическим запросом от ограниченной роли.
            Assert.Equal("ready", await SchemaMaintenance.StatusAsync(runtime, Ct));
            var history = await runtime.Database
                .SqlQueryRaw<int>("""SELECT count(*) AS "Value" FROM product."__EFMigrationsHistory" """)
                .ToListAsync(Ct);
            Assert.True(history.Single() > 0);
            await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(
                "SELECT * FROM public.existing_marker", Ct));
            await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(
                "CREATE TABLE product.forbidden(id int)", Ct));
        }

        var backupBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = "product_backup",
        };
        await using (var backup = ProductDbContext.Create(backupBuilder.ConnectionString))
        {
            Assert.Equal("ready", await SchemaMaintenance.StatusAsync(backup, Ct));
            await Assert.ThrowsAsync<PostgresException>(() => backup.Database.ExecuteSqlRawAsync(
                "SELECT * FROM public.existing_marker", Ct));
            await Assert.ThrowsAsync<PostgresException>(() => backup.Database.ExecuteSqlRawAsync(
                """INSERT INTO product."Source" ("Id","Key","Url") VALUES (gen_random_uuid(),'forbidden','forbidden')""", Ct));
        }

        await using var verify = new NpgsqlConnection(database.ConnectionString);
        await verify.OpenAsync(Ct);
        await using var privileges = new NpgsqlCommand("""
            SELECT has_schema_privilege('product_runtime','public','USAGE'),
                   has_schema_privilege('product_backup','public','USAGE'),
                   has_schema_privilege('product_runtime','public','CREATE'),
                   to_regclass('public."__EFMigrationsHistory"') IS NULL
            """, verify);
        await using var row = await privileges.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        Assert.False(row.GetBoolean(0));
        Assert.False(row.GetBoolean(1));
        Assert.False(row.GetBoolean(2));
        Assert.True(row.GetBoolean(3));
    }

    [Fact]
    public async Task ProvisioningMovesLegacyMigrationHistoryIntoProductSchema()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            // Установка, где история миграций ещё находится в public.
            await using var legacy = new NpgsqlCommand(
                """ALTER TABLE product."__EFMigrationsHistory" SET SCHEMA public""",
                admin);
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        await SchemaMaintenance.ProvisionAsync(
            database.ConnectionString,
            database.RolePassword,
            database.RolePassword,
            database.RolePassword,
            Ct);

        await using (var runtime = database.Open())
        {
            Assert.Equal("ready", await SchemaMaintenance.StatusAsync(runtime, Ct));
        }

        await using var verify = new NpgsqlConnection(database.ConnectionString);
        await verify.OpenAsync(Ct);
        await using var location = new NpgsqlCommand(
            """SELECT to_regclass('public."__EFMigrationsHistory"') IS NULL, to_regclass('product."__EFMigrationsHistory"') IS NOT NULL""",
            verify);
        await using var row = await location.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        Assert.True(row.GetBoolean(0));
        Assert.True(row.GetBoolean(1));
    }

    [Fact]
    public async Task ProvisioningRejectsExistingRuntimeRoleThatCannotLogin()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var disableLogin = new NpgsqlCommand("ALTER ROLE product_runtime NOLOGIN", admin);
            await disableLogin.ExecuteNonQueryAsync(Ct);
        }

        using var operation = new CancellationTokenSource();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMaintenance.ProvisionAsync(
                database.ConnectionString,
                database.RolePassword,
                database.RolePassword,
                database.RolePassword,
                operation.Token));
            Assert.Equal("Служебная роль не разрешает вход; требуется ручная проверка.", exception.Message);
            operation.Cancel();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var admin = new NpgsqlConnection(database.ConnectionString);
            await admin.OpenAsync(cleanup.Token);
            await using var enableLogin = new NpgsqlCommand("ALTER ROLE product_runtime LOGIN", admin);
            await enableLogin.ExecuteNonQueryAsync(cleanup.Token);
        }

        await using var verify = new NpgsqlConnection(database.ConnectionString);
        await verify.OpenAsync(Ct);
        await using var roleState = new NpgsqlCommand("SELECT rolcanlogin FROM pg_roles WHERE rolname='product_runtime'", verify);
        Assert.True((bool)(await roleState.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task RepeatedTelegramConfirmationKeepsContinuousUsernameInterval()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var contact = await Contact(store, revision, "telegram", "first");
        var other = await Contact(store, revision, "telegram", "second");

        var account = await store.ConfirmTelegramAsync(contact, 10001, "SharedName", "первое подтверждение", Ct);
        var original = Assert.Single(await Intervals());
        Assert.Null(original.ValidTo);
        Assert.Equal("sharedname", original.Username);

        // Повторное подтверждение того же имени не создаёт разрыв и новую запись.
        await store.ConfirmTelegramAsync(contact, 10001, "@SharedName", "повторное подтверждение", Ct);
        var continuous = Assert.Single(await Intervals());
        Assert.Equal(original.Id, continuous.Id);
        Assert.Equal(original.ValidFrom, continuous.ValidFrom);
        Assert.Null(continuous.ValidTo);
        Assert.Equal(2, await db.Set<ContactVerification>().CountAsync(Ct));

        // Реальная передача имени другому аккаунту закрывает прежний интервал.
        var otherAccount = await store.ConfirmTelegramAsync(other, 10002, "sharedname", "передача имени", Ct);
        Assert.NotEqual(account, otherAccount);
        var transferred = await Intervals();
        Assert.Equal(2, transferred.Count);
        Assert.Equal(account, Assert.Single(transferred, x => x.ValidTo is not null).AccountId);
        Assert.Equal(otherAccount, Assert.Single(transferred, x => x.ValidTo is null).AccountId);

        // Отсутствующее значение означает «имя не передано» и не меняет историю.
        await store.ConfirmTelegramAsync(contact, 10001, null, "имя не передано", Ct);
        await store.ConfirmTelegramAsync(contact, 10001, "   ", "пустое значение", Ct);
        var unchanged = await Intervals();
        Assert.Equal(2, unchanged.Count);
        Assert.Single(unchanged, x => x.ValidTo is null);

        // Смена имени аккаунта и возврат к имени другого аккаунта остаются связной историей.
        await store.ConfirmTelegramAsync(contact, 10001, "renamed", "смена имени", Ct);
        await store.ConfirmTelegramAsync(contact, 10001, "renamed", "повтор после смены", Ct);
        Assert.Equal(3, (await Intervals()).Count);
        Assert.Equal(2, (await Intervals()).Count(x => x.ValidTo is null));
        await store.ConfirmTelegramAsync(contact, 10001, "sharedname", "возврат имени", Ct);
        var final = await Intervals();
        Assert.Equal(4, final.Count);
        var active = Assert.Single(final, x => x.ValidTo is null);
        Assert.Equal(account, active.AccountId);
        Assert.Equal("sharedname", active.Username);
        Assert.Equal(1, final.Count(x => x.Username == "sharedname" && x.ValidTo is null));
        Assert.Null(active.ValidTo);
        Assert.Equal(3, final.Count(x => x.AccountId == account));
        Assert.Equal(8, await db.Set<ContactVerification>().CountAsync(Ct));

        async Task<List<TelegramUsername>> Intervals()
        {
            db.ChangeTracker.Clear();
            return await db.Set<TelegramUsername>().AsNoTracking().ToListAsync(Ct);
        }
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("review")]
    public async Task ProfileStateChangeTakesPostLockBeforeProfile(string operation)
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);

        await using var concurrent = database.Open();
        await using var transaction = await concurrent.Database.BeginTransactionAsync(Ct);
        await concurrent.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM product."OriginalPost" WHERE "Id"=(SELECT "PostId" FROM product."Profile" WHERE "Id"={profile}) FOR UPDATE""",
            Ct);

        await using var actor = database.Open();
        var applying = operation == "publish"
            ? new ProductStore(actor).PublishAsync(profile, Ct)
            : new ProductStore(actor).ReviewAsync(profile, "администратор", "синтетическая проверка", Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(applying.IsCompleted);

        // Пока строка поста удерживается, операция не имеет права удерживать строку профиля:
        // иначе ArchiveAsync (пост → профиль) и изменение состояния (профиль → пост) дают deadlock.
        await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", Ct);
        await concurrent.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."Profile" SET "State"="State" WHERE "Id"={profile}""", Ct);
        await transaction.CommitAsync(Ct);
        await applying;

        db.ChangeTracker.Clear();
        var stored = await db.Set<Profile>().AsNoTracking().SingleAsync(x => x.Id == profile, Ct);
        Assert.Equal(operation == "publish" ? ProfileState.Published : ProfileState.Reviewed, stored.State);
    }

    [Fact]
    public async Task UnlinkedVerifiedLinkCannotReferenceAnotherContactsVerification()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using var db = database.Open();
        var store = new ProductStore(db);
        var revision = await Import(store, "1");
        var profile = await store.AnalyzeAsync(Analysis(revision), Ct);
        var first = await Contact(store, revision, "telegram", "first");
        var second = await Contact(store, revision, "telegram", "second");
        await store.ConfirmTelegramAsync(first, 10001, null, "первое подтверждение", Ct);
        await store.ConfirmTelegramAsync(second, 10002, null, "второе подтверждение", Ct);
        var link = await store.LinkAsync(profile, first, Guid.Empty, LinkConfidence.VerifiedAccount, "проверенный аккаунт", Ct);
        await store.UnlinkAsync(link.LinkId, "отмена связи", Ct);

        // Исходный контакт наблюдён в ревизии и подтверждён, поэтому подмену отсекает только
        // сопоставление VerificationId с ContactId, которое теперь действует и для отменённой связи.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE product."ProfileLink" SET "ContactId"={second} WHERE "Id"={link.LinkId}""", Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal(first, (await db.Set<ProfileLink>().AsNoTracking().SingleAsync(x => x.Id == link.LinkId, Ct)).ContactId);
    }

    [Fact]
    public async Task ProvisioningConvergesRolePrivilegesToDocumentedSet()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            // Вручную выданные права не должны переживать повторный provisioning.
            await using var grant = new NpgsqlCommand("""GRANT UPDATE, DELETE ON product."Source" TO product_backup""", admin);
            await grant.ExecuteNonQueryAsync(Ct);
        }

        await SchemaMaintenance.ProvisionAsync(
            database.ConnectionString,
            database.RolePassword,
            database.RolePassword,
            database.RolePassword,
            Ct);

        var backupBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = "product_backup" };
        await using (var backup = ProductDbContext.Create(backupBuilder.ConnectionString))
        {
            Assert.Equal("ready", await SchemaMaintenance.StatusAsync(backup, Ct));
            var error = await Assert.ThrowsAsync<PostgresException>(() => backup.Database.ExecuteSqlRawAsync(
                """UPDATE product."Source" SET "Key"='изменено'""", Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task ProvisioningRejectsLegacyHistoryOwnedByAnotherRole()
    {
        RequireDocker();
        await using var database = await cluster.CreateDatabaseAsync(Ct);
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var legacy = new NpgsqlCommand("""
                ALTER TABLE product."__EFMigrationsHistory" SET SCHEMA public;
                ALTER TABLE public."__EFMigrationsHistory" OWNER TO CURRENT_USER
                """, admin);
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => SchemaMaintenance.ProvisionAsync(
            database.ConnectionString,
            database.RolePassword,
            database.RolePassword,
            database.RolePassword,
            Ct));
        Assert.Equal("unexpected_migration_history_owner", error.MessageText);

        // Транзакция provisioning откатилась: история осталась там же и с прежним владельцем.
        await using var verify = new NpgsqlConnection(database.ConnectionString);
        await verify.OpenAsync(Ct);
        await using var location = new NpgsqlCommand("""
            SELECT to_regclass('public."__EFMigrationsHistory"') IS NOT NULL,
                   to_regclass('product."__EFMigrationsHistory"') IS NULL,
                   (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='public."__EFMigrationsHistory"'::regclass) = current_user
            """, verify);
        await using var row = await location.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        Assert.True(row.GetBoolean(0));
        Assert.True(row.GetBoolean(1));
        Assert.True(row.GetBoolean(2));
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