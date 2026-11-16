using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AnketaParser.DockerTests;

/// <summary>Запускает Docker CLI для контейнерных тестов.</summary>
internal sealed class DockerCli
{
    private readonly string _workingDirectory;
    private readonly string _executable;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public DockerCli(string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
        : this(workingDirectory, "docker", environment)
    {
    }

    internal DockerCli(
        string workingDirectory,
        string executable,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _workingDirectory = workingDirectory;
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string>();
    }

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool requireSuccess = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var (name, value) in _environment)
        {
            startInfo.Environment[name] = value;
        }

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
            throw new InvalidOperationException("Не удалось запустить Docker CLI.");
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
                // Процесс уже завершён.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Команда docker {string.Join(' ', arguments)} не завершена за {timeout.TotalSeconds} с."));
        }

        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} завершилась с кодом {process.ExitCode}.{Environment.NewLine}{stderr}");
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