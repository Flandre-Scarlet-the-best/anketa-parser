using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Tests.Support;

/// <summary>Создаёт временный каталог с тестовым <c>appsettings.json</c>.</summary>
internal sealed class TemporaryContentRoot : IDisposable
{
    public TemporaryContentRoot(string? appSettingsJson = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "anketa-parser-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);

        if (appSettingsJson is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "appsettings.json"), appSettingsJson);
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Оставшийся временный каталог не должен прерывать тест.
        }
    }
}

/// <summary>Проверка с заданным поведением.</summary>
internal sealed class StubProbe : IReadinessProbe
{
    private readonly Func<CancellationToken, Task<ProbeResult>> _behavior;

    public StubProbe(string name, Func<CancellationToken, Task<ProbeResult>> behavior)
    {
        Name = name;
        _behavior = behavior;
    }

    public string Name { get; }

    public Task<ProbeResult> CheckAsync(CancellationToken cancellationToken) => _behavior(cancellationToken);
}

/// <summary>Сведения о среде выполнения с фиксированными значениями.</summary>
internal sealed class StubRuntimeSnapshotProvider : IRuntimeSnapshotProvider
{
    public RuntimeSnapshot Capture() => new(
        FrameworkDescription: ".NET test",
        OsDescription: "Test OS",
        Architecture: "X64",
        IsContainer: false,
        MachineName: "test-host",
        ProcessId: 4242,
        Uptime: TimeSpan.FromSeconds(1),
        InstanceName: "test-instance",
        EnvironmentName: "Test");
}