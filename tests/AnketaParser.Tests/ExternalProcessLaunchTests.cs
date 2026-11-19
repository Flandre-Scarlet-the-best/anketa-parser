using AnketaParser.Tests.Support;

namespace AnketaParser.Tests;

/// <summary>
/// Запуск внешних процессов из проверок не должен создавать видимые окна консоли:
/// иначе набор тестов мерцает окнами на экране пользователя.
/// </summary>
public sealed class ExternalProcessLaunchTests
{
    private static readonly string[] ExpectedArguments = ["AnketaParser.Cli.dll", "--help"];

    [Fact]
    public void TestApplicationProcessStartsWithoutConsoleWindow()
    {
        var info = ApplicationProcess.CreateStartInfo(
            Path.GetTempPath(),
            "Cli",
            ["--help"],
            new Dictionary<string, string?> { ["ANKETAPARSER_Tests__Marker"] = "synthetic" });

        Assert.Equal("dotnet", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(Path.GetTempPath(), info.WorkingDirectory);
        Assert.Equal(ExpectedArguments, info.ArgumentList);
        Assert.Equal("synthetic", info.Environment["ANKETAPARSER_Tests__Marker"]);
        Assert.Equal("Production", info.Environment["DOTNET_ENVIRONMENT"]);
    }

    [Fact]
    public void TestApplicationProcessDoesNotInheritHarnessConfiguration()
    {
        // Переменные окружения харнесса не должны протекать в проверяемое приложение.
        var info = ApplicationProcess.CreateStartInfo(Path.GetTempPath(), "Host", [], environment: null);

        Assert.DoesNotContain(
            info.Environment.Keys,
            key => key.StartsWith("ANKETAPARSER_", StringComparison.OrdinalIgnoreCase));
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
    }
}