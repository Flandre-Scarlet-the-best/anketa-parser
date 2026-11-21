using System.Text.Json;

using AnketaParser.Cli;
using AnketaParser.Core;

namespace AnketaParser.Tests;

/// <summary>
/// Проверки командной строки отчёта: разбор аргументов, единый машинный документ,
/// коды завершения и отсутствие файла при неуспехе. Хранилище не требуется.
/// </summary>
[Collection("Окружение процесса")]
public sealed class ReportCommandTests
{
    /// <summary>Наборы аргументов, которые обязаны давать код 2 и один машинный документ.</summary>
    private static readonly string[][] InvalidArgumentSets =
    [
        ["report"],
        ["report", "unknown"],
        ["report", "html"],
        ["report", "html", "--output"],
        ["report", "html", "--output", "out.html", "--unknown"],
        ["report", "html", "--source-key"],
        // Результат не может быть записан в тот же файл, из которого прочитаны метаданные.
        ["report", "html", "--output", "same.json", "--run-metadata", "same.json"],
    ];

    [Fact]
    public async Task HelpStaysTextualAndDescribesTheCommand()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var result = await RunAsync(["report", "help"], root.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("report html --output", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("--run-metadata", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task RootHelpListsReportCommand()
    {
        var result = await RunAsync(["help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("report", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("report help", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidArgumentsProduceOneMachineDocument()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        foreach (var arguments in InvalidArgumentSets)
        {
            var result = await RunAsync(arguments, root.Path);

            Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
            using var document = JsonDocument.Parse(result.Stdout);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("report html", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_usage", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(ExitCodes.InvalidUsage, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.DoesNotContain("out.html", result.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(System.IO.Path.Combine(root.Path, "out.html")));
        }
    }

    [Fact]
    public async Task UnconfiguredStorageStopsBeforeAnyFileIsWritten()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var output = System.IO.Path.Combine(root.Path, "report.html");
        var result = await RunAsync(["report", "html", "--output", output], root.Path);

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("report html", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("not_configured", document.RootElement.GetProperty("status").GetString());
        Assert.NotEmpty(result.Stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task OutputThroughDirectoryLinkCannotReplaceRunMetadata()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var data = System.IO.Path.Combine(root.Path, "data");
        var alias = System.IO.Path.Combine(root.Path, "alias");
        Directory.CreateDirectory(data);
        try
        {
            Directory.CreateSymbolicLink(alias, data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Символические ссылки недоступны в этом окружении.");
        }

        var metadata = System.IO.Path.Combine(data, "run.json");
        await File.WriteAllTextAsync(metadata, "{}", TestContext.Current.CancellationToken);
        var output = System.IO.Path.Combine(alias, "run.json");
        var result = await RunAsync(["report", "html", "--output", output, "--run-metadata", metadata], root.Path);

        // Ссылка на каталог ведёт к тому же файлу: запись отклоняется как неверные аргументы.
        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        Assert.Equal("{}", await File.ReadAllTextAsync(metadata, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SamePathComparisonFollowsCaseInsensitiveFileSystem()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Assert.Skip("Регистрозависимая файловая система: такие файлы различаются.");
        using var root = new Support.TemporaryContentRoot("{}");
        var metadata = System.IO.Path.Combine(root.Path, "run.json");
        var output = System.IO.Path.Combine(root.Path, "RUN.json");
        var result = await RunAsync(["report", "html", "--output", output, "--run-metadata", metadata], root.Path);
        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args, string? contentRootPath = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        // Настройки процесса не должны подменять конфигурацию теста.
        using var environment = Support.ProcessEnvironment.Isolate();
        var exitCode = await CliApplication.RunAsync(
            args, stdout, stderr, TestContext.Current.CancellationToken, contentRootPath);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}