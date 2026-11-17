using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

public static class SchemaMaintenance
{
    public static async Task<string> StatusAsync(ProductDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var expected = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToArray();
        if (applied.Except(expected, StringComparer.Ordinal).Any()) return "newer_or_unknown";
        return applied.SequenceEqual(expected, StringComparer.Ordinal) ? "ready" : "migration_required";
    }

    public static async Task MigrateAsync(ProductDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await StatusAsync(db, ct).ConfigureAwait(false) == "newer_or_unknown")
            throw new InvalidOperationException("Обнаружена неизвестная версия схемы; требуется совместимый мигратор.");
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Явное администрирование новой или существующей БД; не вызывается Host.</summary>
    public static async Task ProvisionAsync(string connectionString, string migrationPassword, string runtimePassword, string backupPassword, CancellationToken ct)
    {
        foreach (var password in new[] { migrationPassword, runtimePassword, backupPassword })
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (role, password) in new[] { ("product_migrator", migrationPassword), ("product_runtime", runtimePassword), ("product_backup", backupPassword) })
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)", connection, transaction);
            exists.Parameters.AddWithValue(role);
            if (!(bool)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            {
                await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", connection, transaction);
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var check = new NpgsqlCommand("SELECT rolcanlogin, rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid) FROM pg_roles WHERE rolname=$1", connection, transaction);
            check.Parameters.AddWithValue(role);
            await using (var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Служебная роль исчезла во время подготовки.");
                if (!reader.GetBoolean(0))
                    throw new InvalidOperationException("Служебная роль не разрешает вход; требуется ручная проверка.");
                if (reader.GetBoolean(1))
                    throw new InvalidOperationException("Служебная роль имеет неожиданные привилегии; требуется ручная проверка.");
            }
            await using var format = new NpgsqlCommand("SELECT format('ALTER ROLE %I PASSWORD %L', $1::text, $2::text)", connection, transaction);
            format.Parameters.AddWithValue(role); format.Parameters.AddWithValue(password);
            var sql = (string)(await format.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            await using var alter = new NpgsqlCommand(sql, connection, transaction);
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand("""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            CREATE SCHEMA IF NOT EXISTS product AUTHORIZATION product_migrator;
            CREATE SCHEMA IF NOT EXISTS private AUTHORIZATION product_migrator;
            DO $$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname IN ('product','private')
                AND nspowner<>(SELECT oid FROM pg_roles WHERE rolname='product_migrator'))
              THEN RAISE EXCEPTION 'unexpected_schema_owner'; END IF;
            END $$;
            GRANT USAGE, CREATE ON SCHEMA public TO product_migrator;
            -- История миграций принадлежит схеме продукта: архив и права не зависят от public.
            DO $ef_history$
            BEGIN
              IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL
                AND to_regclass('product."__EFMigrationsHistory"') IS NULL THEN
                EXECUTE 'ALTER TABLE public."__EFMigrationsHistory" SET SCHEMA product';
              END IF;
              IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL
                AND to_regclass('product."__EFMigrationsHistory"') IS NOT NULL THEN
                RAISE EXCEPTION 'ambiguous_migration_history';
              END IF;
              -- SET SCHEMA не меняет владельца: мигратор обязан остаться владельцем читаемой истории.
              IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='product' AND c.relname='__EFMigrationsHistory'
                  AND c.relowner<>(SELECT oid FROM pg_roles WHERE rolname='product_migrator'))
              THEN RAISE EXCEPTION 'unexpected_migration_history_owner'; END IF;
            END
            $ef_history$;
            -- Повторный provisioning приводит роли к документированному набору прав:
            -- вручную выданные права на таблицы и последовательности снимаются перед выдачей.
            REVOKE ALL ON SCHEMA product, private FROM product_runtime, product_backup;
            REVOKE ALL ON ALL TABLES IN SCHEMA product, private FROM product_runtime, product_backup;
            REVOKE ALL ON ALL SEQUENCES IN SCHEMA product, private FROM product_runtime, product_backup;
            GRANT USAGE ON SCHEMA product, private TO product_runtime, product_backup;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA product, private TO product_runtime;
            GRANT SELECT ON ALL TABLES IN SCHEMA product, private TO product_backup;
            GRANT USAGE ON ALL SEQUENCES IN SCHEMA product, private TO product_runtime;
            GRANT SELECT ON ALL SEQUENCES IN SCHEMA product, private TO product_backup;
            ALTER DEFAULT PRIVILEGES FOR ROLE product_migrator IN SCHEMA product, private
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO product_runtime;
            ALTER DEFAULT PRIVILEGES FOR ROLE product_migrator IN SCHEMA product, private
                GRANT SELECT ON TABLES TO product_backup;
            ALTER DEFAULT PRIVILEGES FOR ROLE product_migrator IN SCHEMA product, private
                GRANT USAGE ON SEQUENCES TO product_runtime;
            ALTER DEFAULT PRIVILEGES FOR ROLE product_migrator IN SCHEMA product, private
                GRANT SELECT ON SEQUENCES TO product_backup;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // CONNECT нужен только к целевой БД; имя берём из серверного контекста.
        await using var databaseGrant = new NpgsqlCommand("SELECT format('REVOKE CREATE, TEMPORARY ON DATABASE %I FROM PUBLIC; GRANT CONNECT ON DATABASE %I TO product_migrator, product_runtime, product_backup', current_database(), current_database())", connection, transaction);
        var grant = (string)(await databaseGrant.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        await using var applyGrant = new NpgsqlCommand(grant, connection, transaction);
        await applyGrant.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}