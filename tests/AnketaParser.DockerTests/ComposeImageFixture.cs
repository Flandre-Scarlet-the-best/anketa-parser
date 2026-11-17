[assembly: AssemblyFixture(typeof(AnketaParser.DockerTests.ComposeImageFixture))]

namespace AnketaParser.DockerTests;

/// <summary>
/// Собирает образы стека один раз на весь прогон набора Docker-тестов.
/// </summary>
/// <remarks>
/// Наборы <see cref="ComposeStackSmokeTests"/> и <see cref="BackupRecoveryTests"/> выполняются
/// параллельно и каждый проверяет сборку собственных целей Dockerfile. Без общего
/// предварительного шага обе проверки запускали <c>docker compose build</c> одновременно, и на
/// холодном кеше сборщика одинаковые стадии (восстановление, компиляция, публикация) выполнялись
/// дважды. Общая сборка наполняет кеш один раз, а собственные шаги сборки в проверках остаются
/// и превращаются в попадание в кеш, поэтому проверка собираемости образов не ослабляется.
/// </remarks>
public sealed class ComposeImageFixture : IAsyncLifetime
{
    /// <summary>Переменная, без которой контейнерные проверки не запускаются.</summary>
    public const string EnableVariable = "ANKETAPARSER_DOCKER_TESTS";

    private const string ProjectName = "anketa-parser-test-images";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Корень репозитория, относительно которого собираются образы стека.</summary>
    private readonly Lazy<string> _repositoryRoot = new(ComposeStackSmokeTests.FindRepositoryRoot);

    public string RepositoryRoot => _repositoryRoot.Value;

    /// <summary>Признак явного включения контейнерных проверок.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var docker = new DockerCli(RepositoryRoot);
        await docker.RunAsync(
            [
                "compose",
                "-p", ProjectName,
                "-f", Path.Combine(RepositoryRoot, "compose.yaml"),
                "build",
                "host",
                "maintenance",
            ],
            BuildTimeout,
            CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}