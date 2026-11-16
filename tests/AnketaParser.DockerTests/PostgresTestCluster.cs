using System.Globalization;

using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace AnketaParser.DockerTests;

/// <summary>
/// Один контейнер PostgreSQL на весь набор проверок сохранности продукта.
/// </summary>
/// <remarks>
/// Каждая проверка получает собственную базу: схема <c>product</c>/<c>private</c>, права,
/// миграции и данные изолированы по базе, поэтому общий сервер не разделяет состояние тестов.
/// Запуск контейнера, ожидание готовности сервера и удаление контейнера выполняются один раз
/// за прогон, а не для каждой проверки. Роли создаются на уровне кластера при первом
/// вызове подготовки ролей и затем повторно проверяются на каждой базе.
/// </remarks>
public sealed class PostgresTestCluster : IAsyncLifetime
{
    private const string EnableVariable = "ANKETAPARSER_DOCKER_TESTS";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DockerCli? _docker;
    private string _containerName = string.Empty;
    private string _rolePassword = string.Empty;
    private string _adminConnectionString = string.Empty;

    /// <summary>Признак явного включения контейнерных проверок.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var root = ComposeStackSmokeTests.FindRepositoryRoot();
        _docker = new DockerCli(root);
        _containerName = "anketa-persistence-test-" + Guid.NewGuid().ToString("N");
        _rolePassword = Guid.NewGuid().ToString("N");
        try
        {
            await _docker.RunAsync(
                [
                    "run", "-d",
                    "--name", _containerName,
                    "-e", "POSTGRES_PASSWORD=" + _rolePassword,
                    "-p", "127.0.0.1::5432",
                    "postgres:18.6-alpine",
                ],
                StartupTimeout,
                CancellationToken.None);

            var port = await _docker.RunAsync(
                ["port", _containerName, "5432"],
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            _adminConnectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = int.Parse(port.Trim().Split(':')[^1], CultureInfo.InvariantCulture),
                Database = "postgres",
                Username = "postgres",
                Password = _rolePassword,
            }.ConnectionString;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(_adminConnectionString);
                    await connection.OpenAsync(CancellationToken.None);
                    break;
                }
                catch (NpgsqlException) when (attempt < 60)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                }
            }
        }
        catch
        {
            // Неудачный запуск не должен оставлять контейнер и не должен подменять исходную ошибку.
            NpgsqlConnection.ClearAllPools();
            if (_docker is not null)
            {
                await _docker.RunAsync(
                    ["rm", "-fv", _containerName],
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None,
                    requireSuccess: false);
            }

            _docker = null;
            throw;
        }
    }

    /// <summary>
    /// Создаёт изолированную базу проверки и повторяет на ней provisioning и миграции,
    /// включая повторное применение операций.
    /// </summary>
    public async Task<TestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_docker is null)
        {
            throw new InvalidOperationException("Кластер PostgreSQL не запущен; требуется ANKETAPARSER_DOCKER_TESTS=1.");
        }

        var name = "persistence_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var create = new NpgsqlCommand(
                string.Create(CultureInfo.InvariantCulture, $"CREATE DATABASE \"{name}\""),
                admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var database = new TestDatabase(
            new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString,
            _rolePassword);
        try
        {
            await database.PrepareAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_docker is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await _docker.RunAsync(["rm", "-fv", _containerName], TimeSpan.FromSeconds(30), CancellationToken.None);
        _docker = null;
    }
}

/// <summary>Изолированная база проверки внутри общего контейнера PostgreSQL.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly List<string> _ownedDatabases;

    internal TestDatabase(string connectionString, string rolePassword)
    {
        ConnectionString = connectionString;
        RolePassword = rolePassword;
        _ownedDatabases = [new NpgsqlConnectionStringBuilder(connectionString).Database!];
    }

    internal string ConnectionString { get; }

    internal string RolePassword { get; }

    /// <summary>Повторяет подготовку прежнего персонального контейнера на этой базе.</summary>
    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var marker = new NpgsqlCommand(
                "CREATE TABLE public.existing_marker(id int); INSERT INTO public.existing_marker VALUES(42)",
                connection);
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }

        await SchemaMaintenance.ProvisionAsync(ConnectionString, RolePassword, RolePassword, RolePassword, cancellationToken);
        await SchemaMaintenance.ProvisionAsync(ConnectionString, RolePassword, RolePassword, RolePassword, cancellationToken);
        await using (var admin = Open(runtime: false))
        {
            Assert.Equal("migration_required", await SchemaMaintenance.StatusAsync(admin, cancellationToken));
            await SchemaMaintenance.MigrateAsync(admin, cancellationToken);
            await SchemaMaintenance.MigrateAsync(admin, cancellationToken);
        }

        await using var verify = new NpgsqlConnection(ConnectionString);
        await verify.OpenAsync(cancellationToken);
        await using var query = new NpgsqlCommand("SELECT id FROM public.existing_marker", verify);
        Assert.Equal(42, await query.ExecuteScalarAsync(cancellationToken));
    }

    public ProductDbContext Open(bool runtime = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = runtime ? "product_runtime" : "product_migrator",
        };
        return new(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(builder.ConnectionString).Options);
    }

    public async Task<ProductDbContext> CreateConflictDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "conflict_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var create = new NpgsqlCommand(
                string.Create(CultureInfo.InvariantCulture, $"CREATE DATABASE \"{name}\" OWNER product_migrator"),
                admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        _ownedDatabases.Add(name);
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = name,
            Username = "product_migrator",
        };
        var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(builder.ConnectionString).Options);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE SCHEMA product; CREATE TABLE product."Source"(marker int); INSERT INTO product."Source" VALUES(42)""",
            cancellationToken);
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        // Удалять базы нужно из соединения с другой базой кластера.
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        foreach (var name in _ownedDatabases)
        {
            await using var drop = new NpgsqlCommand(
                string.Create(CultureInfo.InvariantCulture, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)"),
                admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}