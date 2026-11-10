using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AnketaParser.DockerTests;

/// <summary>Thin wrapper around the <c>docker</c> CLI used by the smoke test.</summary>
internal sealed class DockerCli
{
    private readonly string _workingDirectory;

    public DockerCli(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool requireSuccess = true)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("The docker CLI could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            throw new TimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"docker {string.Join(' ', arguments)} did not finish within {timeout.TotalSeconds}s."));
        }

        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{stderr}");
        }

        return stdout.ToString().Trim();
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }
}