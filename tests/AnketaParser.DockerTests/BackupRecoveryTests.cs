using System.Text.Json;

namespace AnketaParser.DockerTests;

public sealed class BackupRecoveryTests(ComposeImageFixture images)
{
    [Fact]
    public async Task BackupRestoresRelationshipsAndArchiveAndFailuresPreserveLastCopy()
    {
        if (!ComposeImageFixture.Enabled) Assert.Skip($"Требуется {ComposeImageFixture.EnableVariable}=1.");
        var ct = TestContext.Current.CancellationToken;
        var root = images.RepositoryRoot;
        var password = Guid.NewGuid().ToString("N");
        var docker = new DockerCli(root, new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = password,
            ["POSTGRES_USER"] = "test_admin",
            ["POSTGRES_DB"] = "test_product",
        });
        var project = "anketa-backup-test-" + Guid.NewGuid().ToString("N");
        string[] prefix = ["compose", "-p", project, "-f", Path.Combine(root, "compose.yaml")];
        var timeout = TimeSpan.FromMinutes(3);
        Task<string> Sql(string database, string sql) => docker.RunAsync([.. prefix, "exec", "-T", "postgres", "psql", "-U", "test_admin", "-d", database, "-v", "ON_ERROR_STOP=1", "-tAc", sql], timeout, ct);
        Task<string> Cli(params string[] args) => docker.RunAsync([.. prefix, "run", "--rm", .. args], timeout, ct);
        try
        {
            await docker.RunAsync([.. prefix, "up", "-d", "--wait", "postgres"], timeout, ct);
            await docker.RunAsync([.. prefix, "build", "maintenance"], TimeSpan.FromMinutes(12), ct);
            await Cli("-e", $"ANKETAPARSER_Database__ConnectionString=Host=postgres;Database=test_product;Username=test_admin;Password={password}", "maintenance", "storage", "provision");
            await Cli("maintenance", "storage", "migrate");
            await Sql("test_product", """
                INSERT INTO product."Source" VALUES ('10000000-0000-0000-0000-000000000001','synthetic','https://example.invalid');
                INSERT INTO product."OriginalPost" ("Id","SourceId","ExternalId","Url","LastSeenAt","Excluded")
                  VALUES ('20000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','1','https://example.invalid/1',now(),false);
                INSERT INTO product."PostRevision" VALUES ('30000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','synthetic-hash','Синтетический пост','{}',now());
                INSERT INTO product."Analysis" ("Id","RevisionId","Processor","SchemaVersion","Observations","Assessments","CreatedAt")
                  VALUES ('40000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001','synthetic','v1','{}','{}',now());
                INSERT INTO product."Profile" VALUES ('50000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001',4,now());
                UPDATE product."OriginalPost" SET "Excluded"=true;
                INSERT INTO product."Contact" VALUES ('60000000-0000-0000-0000-000000000001','telegram','synthetic');
                INSERT INTO product."ContactObservation" VALUES ('70000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001','@synthetic',0.9,'синтетический источник',now());
                INSERT INTO private."TelegramAccount" VALUES ('80000000-0000-0000-0000-000000000001',10001);
                INSERT INTO product."ContactVerification" VALUES ('90000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','80000000-0000-0000-0000-000000000001','синтетическая проверка',now());
                INSERT INTO product."Anon" VALUES ('10000000-0000-0000-0000-000000000002',now());
                INSERT INTO product."ProfileLink" ("Id","ProfileId","ContactId","AnonId","Confidence","Provenance","LinkedAt","VerificationId")
                  VALUES ('20000000-0000-0000-0000-000000000002','50000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000002',1,'проверен аккаунт',now(),'90000000-0000-0000-0000-000000000001');
                INSERT INTO private."BotUser" VALUES ('30000000-0000-0000-0000-000000000002',20001,'{}');
                INSERT INTO product."BlacklistEntry" VALUES ('40000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000002','синтетическая причина',now());
                INSERT INTO product."Complaint" VALUES ('50000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000002','50000000-0000-0000-0000-000000000001','60000000-0000-0000-0000-000000000001','чужой контакт',now());
                INSERT INTO product."ModerationDecision" ("Id","ProfileId","ComplaintId","Actor","Action","Reason","CreatedAt")
                  VALUES ('60000000-0000-0000-0000-000000000002','50000000-0000-0000-0000-000000000001','50000000-0000-0000-0000-000000000002','администратор','archive','синтетическая подделка',now());
                """);
            var result = await Cli("maintenance", "storage", "backup");
            using var document = JsonDocument.Parse(result);
            var name = document.RootElement.GetProperty("archive").GetString()!;
            await Cli("maintenance", "storage", "backup-verify", name);
            await Sql("postgres", "CREATE DATABASE test_restore OWNER product_migrator");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgRestore=/bin/false",
                "-e", $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password=local-migration-password",
                "maintenance", "storage", "restore-empty", name));
            await Cli("-e", "ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password=local-migration-password",
                "maintenance", "storage", "restore-empty", name);
            const string fingerprint = """
                SELECT json_build_array(
                  (SELECT json_agg(t ORDER BY "Id") FROM product."OriginalPost" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."PostRevision" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."Analysis" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."Profile" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."ContactObservation" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM private."TelegramAccount" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."ProfileLink" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."ContactVerification" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."BlacklistEntry" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."Complaint" t),
                  (SELECT json_agg(t ORDER BY "Id") FROM product."ModerationDecision" t),
                  (SELECT json_agg(t ORDER BY "MigrationId") FROM public."__EFMigrationsHistory" t))::text
                """;
            Assert.Equal(await Sql("test_product", fingerprint), await Sql("test_restore", fingerprint));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e",
                "ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password=local-migration-password",
                "maintenance", "storage", "restore-empty", name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgDump=/bin/false", "maintenance", "storage", "backup"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__MaxBytes=1", "maintenance", "storage", "backup"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__Directory=/proc/unwritable", "maintenance", "storage", "backup"));
            await Cli("maintenance", "storage", "backup-rotate");
            Assert.Contains(name, await Cli("maintenance", "storage", "backup-list"), StringComparison.Ordinal);
            await Cli("maintenance", "storage", "backup-verify", name);
            var status = await Cli("maintenance", "storage", "backup-status");
            Assert.Contains("LastRestoreVerified", status, StringComparison.Ordinal);
            Assert.Contains("backup_failed", status, StringComparison.Ordinal);
            await docker.RunAsync([.. prefix, "run", "--rm", "--entrypoint", "/bin/sh", "maintenance", "-c",
                "printf broken > /backups/corrupt.dump; printf bad > /backups/corrupt.dump.sha256"], timeout, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("maintenance", "storage", "backup-verify", "corrupt.dump"));
        }
        finally
        {
            await docker.RunAsync([.. prefix, "down", "--volumes", "--remove-orphans"], timeout, CancellationToken.None);
        }
    }
}