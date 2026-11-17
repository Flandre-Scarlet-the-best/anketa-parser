using System.Text.Json;

namespace AnketaParser.DockerTests;

public sealed class BackupRecoveryTests(ComposeImageFixture images)
{
    [Fact]
    public async Task BackupRestoresProductWithRestrictedRoleAndPreservesPublishedStateAcrossFailures()
    {
        if (!ComposeImageFixture.Enabled) Assert.Skip($"Требуется {ComposeImageFixture.EnableVariable}=1.");
        var ct = TestContext.Current.CancellationToken;
        var root = images.RepositoryRoot;
        var password = Guid.NewGuid().ToString("N");
        var migrationPassword = Guid.NewGuid().ToString("N");
        var docker = new DockerCli(root, new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = password,
            ["POSTGRES_USER"] = "test_admin",
            ["POSTGRES_DB"] = "test_product",
            ["PRODUCT_MIGRATION_PASSWORD"] = migrationPassword,
        });
        var project = "anketa-backup-test-" + Guid.NewGuid().ToString("N");
        var daemons = new List<string>();
        string[] prefix = ["compose", "-p", project, "-f", Path.Combine(root, "compose.yaml")];
        var timeout = TimeSpan.FromMinutes(3);
        Task<string> Sql(string database, string sql) => docker.RunAsync([.. prefix, "exec", "-T", "postgres", "psql", "-U", "test_admin", "-d", database, "-v", "ON_ERROR_STOP=1", "-tAc", sql], timeout, ct);
        Task<string> Cli(params string[] args) => docker.RunAsync([.. prefix, "run", "--rm", .. args], timeout, ct);
        Task<string> Shell(string script) => docker.RunAsync([.. prefix, "run", "--rm", "--entrypoint", "/bin/sh", "maintenance", "-c", script], timeout, ct);
        try
        {
            await docker.RunAsync([.. prefix, "up", "-d", "--wait", "postgres"], timeout, ct);
            await docker.RunAsync([.. prefix, "build", "maintenance"], TimeSpan.FromMinutes(12), ct);
            await Sql("test_product", """
                CREATE TABLE public.existing_marker(id int); INSERT INTO public.existing_marker VALUES(42);
                CREATE TABLE public."existing$marker"(id int); INSERT INTO public."existing$marker" VALUES(43);
                CREATE SCHEMA "unrelated$schema"; CREATE TABLE "unrelated$schema".marker(id int);
                INSERT INTO "unrelated$schema".marker VALUES(44)
                """);
            await Cli("-e", "ANKETAPARSER_Database__ConnectionString=Host=postgres;Database=test_product;Username=test_admin",
                "-e", $"ANKETAPARSER_Database__Password={password}", "maintenance", "storage", "provision");
            await Cli("maintenance", "storage", "migrate");
            Assert.Equal("f", await Sql("test_product", "SELECT has_table_privilege('product_backup','public.existing_marker','SELECT')"));
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
            Assert.Equal("42", await Sql("test_product", "SELECT id FROM public.existing_marker"));
            Assert.Equal("43", await Sql("test_product", """SELECT id FROM public."existing$marker" """));
            Assert.Equal("44", await Sql("test_product", """SELECT id FROM "unrelated$schema".marker"""));
            await Sql("postgres", "CREATE DATABASE test_prepared OWNER product_migrator");
            await Sql("test_prepared", "CREATE SCHEMA product AUTHORIZATION product_migrator; CREATE SCHEMA private AUTHORIZATION product_migrator");
            await Shell("printf '#!/bin/sh\ntouch /backups/restore-was-run\nexit 1\n' > /backups/restore-must-not-run; chmod 700 /backups/restore-must-not-run");
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgRestore=/backups/restore-must-not-run",
                "-e", $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_prepared;Username=product_migrator;Password={migrationPassword}",
                "maintenance", "storage", "restore-empty", name));
            Assert.Contains("другую пустую БД", rejected.Message, StringComparison.Ordinal);
            Assert.Equal("не запущено", await Shell("test ! -e /backups/restore-was-run && printf 'не запущено'"));
            Assert.Equal("0", await Sql("test_prepared", "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('product','private')"));
            await Sql("postgres", "CREATE DATABASE test_nonrelation OWNER product_migrator");
            await Sql("test_nonrelation", """CREATE COLLATION public.synthetic_order FROM pg_catalog."C" """);
            var nonrelation = await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgRestore=/backups/restore-must-not-run",
                "-e", $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_nonrelation;Username=product_migrator;Password={migrationPassword}",
                "maintenance", "storage", "restore-empty", name));
            Assert.Contains("другую пустую БД", nonrelation.Message, StringComparison.Ordinal);
            Assert.Equal("не запущено", await Shell("test ! -e /backups/restore-was-run && printf 'не запущено'"));
            // Совместимость с состоянием, записанным до появления привязки метаданных к архиву.
            await Shell("sed -i 's/,\"ArchiveLength\":[0-9]*//;s/,\"ArchiveDigest\":\"[A-F0-9]*\"//' /backups/status.json");
            await Sql("postgres", "CREATE DATABASE test_restore OWNER product_migrator");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgRestore=/bin/false",
                "-e", $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password={migrationPassword}",
                "maintenance", "storage", "restore-empty", name));
            await Cli("-e", $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password={migrationPassword}",
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
            Assert.Equal("", await Sql("test_restore", "SELECT to_regclass('public.existing_marker')"));
            Assert.Equal("", await Sql("test_restore", """SELECT to_regclass('public."existing$marker"')"""));
            Assert.Equal("0", await Sql("test_restore", "SELECT count(*) FROM pg_namespace WHERE nspname='unrelated$schema'"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e",
                $"ANKETAPARSER_Restore__ConnectionString=Host=postgres;Database=test_restore;Username=product_migrator;Password={migrationPassword}",
                "maintenance", "storage", "restore-empty", name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__PgDump=/bin/false", "maintenance", "storage", "backup"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__MaxBytes=1", "maintenance", "storage", "backup"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__Directory=/proc/unwritable", "maintenance", "storage", "backup"));
            await Cli("maintenance", "storage", "backup-rotate");
            Assert.Contains(name, await Cli("maintenance", "storage", "backup-list"), StringComparison.Ordinal);
            await Cli("maintenance", "storage", "backup-verify", name);
            var status = await Cli("maintenance", "storage", "backup-status");
            using var statusDocument = JsonDocument.Parse(status);
            Assert.True(statusDocument.RootElement.GetProperty("LastRestoreVerified").TryGetDateTimeOffset(out var restoredAt));
            Assert.InRange(restoredAt, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow);
            Assert.True(statusDocument.RootElement.GetProperty("ArchiveLength").GetInt64() > 0);
            Assert.Equal(64, statusDocument.RootElement.GetProperty("ArchiveDigest").GetString()!.Length);
            Assert.Contains("backup_failed", status, StringComparison.Ordinal);
            await Shell("dd if=/dev/zero of=/backups/manual.dump bs=1048576 count=1 2>/dev/null");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__MaxBytes=1048576", "maintenance", "storage", "backup"));
            using var rotation = JsonDocument.Parse(await Cli("maintenance", "storage", "backup-status"));
            var latest = rotation.RootElement.GetProperty("Archive").GetString()!;
            Assert.NotEqual(name, latest);
            Assert.Equal("rotation_failed", rotation.RootElement.GetProperty("FailureCategory").GetString());
            Assert.Equal(JsonValueKind.Null, rotation.RootElement.GetProperty("LastRestoreVerified").ValueKind);
            Assert.True(rotation.RootElement.GetProperty("LastSuccess").GetDateTimeOffset() > restoredAt);
            await Cli("maintenance", "storage", "backup-verify", latest);
            await Cli("maintenance", "storage", "backup-verify", name);
            var listed = JsonSerializer.Deserialize<string[]>(await Cli("maintenance", "storage", "backup-list"))!;
            Assert.Contains(latest, listed);
            Assert.Contains(name, listed);
            Assert.Contains("manual.dump", listed);
            var recovered = await Cli("-e", "ANKETAPARSER_Backup__ExternalDirectory=/backups/external", "maintenance", "storage", "backup");
            var beforeFailure = await Cli("maintenance", "storage", "backup-list");
            using var recoveredDocument = JsonDocument.Parse(recovered);
            var recoveredName = recoveredDocument.RootElement.GetProperty("archive").GetString()!;
            Assert.Equal("совпадает", await Shell($"cmp /backups/{recoveredName} /backups/external/{recoveredName} && cmp /backups/{recoveredName}.sha256 /backups/external/{recoveredName}.sha256 && printf 'совпадает'"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__ExternalDirectory=/proc/forbidden", "maintenance", "storage", "backup"));
            Assert.Equal(beforeFailure, await Cli("maintenance", "storage", "backup-list"));
            using var externalFailure = JsonDocument.Parse(await Cli("maintenance", "storage", "backup-status"));
            Assert.Equal(recoveredName, externalFailure.RootElement.GetProperty("Archive").GetString());
            Assert.Equal("backup_failed", externalFailure.RootElement.GetProperty("FailureCategory").GetString());
            await Cli("maintenance", "storage", "backup-verify", recoveredName);
            await Cli("maintenance", "storage", "backup-verify", name);
            Assert.Equal("", await Shell("find /backups -name '*.partial' -print"));
            await Shell("printf '#!/bin/sh\ntouch /backups/dump-started\nsleep 60\n' > /backups/slow-dump; chmod 700 /backups/slow-dump");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("-e", "ANKETAPARSER_Backup__TimeoutSeconds=1",
                "-e", "ANKETAPARSER_Backup__PgDump=/backups/slow-dump", "maintenance", "storage", "backup"));
            using var timedOut = JsonDocument.Parse(await Cli("maintenance", "storage", "backup-status"));
            Assert.Equal("timeout", timedOut.RootElement.GetProperty("FailureCategory").GetString());
            Assert.Equal(recoveredName, timedOut.RootElement.GetProperty("Archive").GetString());
            foreach (var duringDump in new[] { true, false })
            {
                await Shell("rm -f /backups/dump-started");
                var daemon = project + (duringDump ? "-dump" : "-wait");
                daemons.Add(daemon);
                await docker.RunAsync([.. prefix, "run", "-d", "--no-deps", "--name", daemon,
                    "-e", "ANKETAPARSER_Backup__TimeoutSeconds=120", "-e", "ANKETAPARSER_Backup__PgDump=" + (duringDump ? "/backups/slow-dump" : "/bin/false"),
                    "maintenance", "storage", "backup-daemon"], timeout, ct);
                var condition = duringDump ? "test -e /backups/dump-started" : "grep -q backup_failed /backups/status.json";
                await docker.RunAsync(["exec", daemon, "/bin/sh", "-c", $"i=0; until {condition}; do i=$((i+1)); test $i -lt 100 || exit 1; sleep 0.1; done"], TimeSpan.FromSeconds(15), ct);
                await docker.RunAsync(["stop", "--time", "10", daemon], TimeSpan.FromSeconds(20), ct);
                Assert.Equal("0", await docker.RunAsync(["inspect", "--format", "{{.State.ExitCode}}", daemon], timeout, ct));
                using var stopped = JsonDocument.Parse(await Cli("maintenance", "storage", "backup-status"));
                Assert.Equal(duringDump ? "cancelled" : "backup_failed", stopped.RootElement.GetProperty("FailureCategory").GetString());
                Assert.Equal(recoveredName, stopped.RootElement.GetProperty("Archive").GetString());
                Assert.Equal(beforeFailure, await Cli("maintenance", "storage", "backup-list"));
                Assert.Equal("", await Shell("find /backups -name '*.partial' -print"));
            }
            await Cli("maintenance", "storage", "backup-verify", name);
            await Cli("maintenance", "storage", "backup-verify", recoveredName);
            await docker.RunAsync([.. prefix, "run", "--rm", "--entrypoint", "/bin/sh", "maintenance", "-c",
                "printf broken > /backups/corrupt.dump; printf bad > /backups/corrupt.dump.sha256"], timeout, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cli("maintenance", "storage", "backup-verify", "corrupt.dump"));
        }
        finally
        {
            foreach (var daemon in daemons)
                await docker.RunAsync(["rm", "-f", daemon], timeout, CancellationToken.None, requireSuccess: false);
            await docker.RunAsync([.. prefix, "down", "--volumes", "--remove-orphans"], timeout, CancellationToken.None);
        }
    }
}