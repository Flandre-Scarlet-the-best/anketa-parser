using System.Diagnostics;

namespace AnketaParser.Tests.Support;

internal static class ApplicationProcess
{
    /// <summary>
    /// Параметры запуска проверяемого приложения. Служебные окна консоли не создаются:
    /// вывод забирается перенаправленными потоками, а <c>CreateNoWindow</c> убирает мерцание
    /// окон при каждом запуске внешнего процесса из тестов.
    /// </summary>
    /// <param name="rootPath">Рабочий каталог запуска.</param>
    /// <param name="application">Имя приложения: <c>AnketaParser.{application}.dll</c>.</param>
    /// <param name="arguments">Аргументы командной строки.</param>
    /// <param name="environment">Дополнительные переменные окружения.</param>
    /// <returns>Параметры запуска без окна консоли.</returns>
    internal static ProcessStartInfo CreateStartInfo(
        string rootPath,
        string application,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (key.StartsWith("ANKETAPARSER_", StringComparison.OrdinalIgnoreCase) || key == "DOTNET_ENVIRONMENT")
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        startInfo.ArgumentList.Add($"AnketaParser.{application}.dll");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string application,
        string json,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var root = new TemporaryContentRoot();
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            if (Path.GetExtension(file) == ".dll" ||
                file.EndsWith(".deps.json", StringComparison.Ordinal) ||
                file.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(root.Path, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(root.Path, "appsettings.json"), json);
        var startInfo = CreateStartInfo(root.Path, application, arguments, environment);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}