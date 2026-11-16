using System.Diagnostics;

namespace AnketaParser.Tests.Support;

internal static class ApplicationProcess
{
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
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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