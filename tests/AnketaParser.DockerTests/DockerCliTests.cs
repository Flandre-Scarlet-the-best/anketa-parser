namespace AnketaParser.DockerTests;

public sealed class DockerCliTests
{
    [Fact]
    public async Task RunAsyncPassesConfiguredEnvironmentToChildProcess()
    {
        const string variableName = "POSTGRES_USER";
        const string expectedValue = "smoke_user";
        var environment = new Dictionary<string, string> { [variableName] = expectedValue };
        var isWindows = OperatingSystem.IsWindows();
        var executable = isWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        var arguments = isWindows
            ? new[] { "/d", "/c", $"echo %{variableName}%" }
            : new[] { "-c", $"printf '%s' \"${variableName}\"" };
        var docker = new DockerCli(Path.GetTempPath(), executable, environment);

        var result = await docker.RunAsync(arguments, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(expectedValue, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitDistinguishesCallerCancellationFromTimeout(bool cancelCaller)
    {
        if (Environment.GetEnvironmentVariable("ANKETAPARSER_DOCKER_TESTS") != "1")
        {
            Assert.Skip("Задайте ANKETAPARSER_DOCKER_TESTS=1 для проверки отмены Docker CLI.");
        }

        var docker = new DockerCli(ComposeStackSmokeTests.FindRepositoryRoot());
        using var caller = new CancellationTokenSource();
        if (cancelCaller)
        {
            caller.CancelAfter(TimeSpan.FromMilliseconds(100));
        }

        // docker events ожидает события до внешней отмены либо собственного таймаута.
        var run = docker.RunAsync(["events", "--filter", "label=anketa-parser-synthetic-cancellation"],
            cancelCaller ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(100), caller.Token);
        if (cancelCaller)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => run);
        }
    }
}