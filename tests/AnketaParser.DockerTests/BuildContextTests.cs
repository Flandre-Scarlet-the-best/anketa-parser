namespace AnketaParser.DockerTests;

public sealed class BuildContextTests
{
    [Fact]
    public async Task NestedLocalSecretsNeverReachBuilderCopy()
    {
        if (Environment.GetEnvironmentVariable("ANKETAPARSER_DOCKER_TESTS") != "1")
        {
            Assert.Skip("Задайте ANKETAPARSER_DOCKER_TESTS=1 для проверки контекста Docker.");
        }

        var scratch = Directory.CreateTempSubdirectory("anketa-parser-context-");
        var context = Directory.CreateDirectory(Path.Combine(scratch.FullName, "input"));
        var output = Path.Combine(scratch.FullName, "output");
        try
        {
            File.Copy(Path.Combine(ComposeStackSmokeTests.FindRepositoryRoot(), ".dockerignore"),
                Path.Combine(context.FullName, ".dockerignore"));
            File.WriteAllText(Path.Combine(context.FullName, "Dockerfile"), "FROM scratch\nCOPY . /context/\n");
            foreach (var directory in new[] { "", "src/AnketaParser.Host", "src/deep/nested", ".codex/local" })
            {
                var target = Directory.CreateDirectory(Path.Combine(context.FullName, directory));
                foreach (var name in new[] { ".env", ".env.private", "appsettings.Local.json", "llm.toml", "model.gguf" })
                {
                    File.WriteAllText(Path.Combine(target.FullName, name), "synthetic-secret-sentinel");
                }

                File.WriteAllText(Path.Combine(target.FullName, "appsettings.json"), "public-settings-marker");
            }

            var docker = new DockerCli(context.FullName);
            await docker.RunAsync(["build", "--no-cache", "--output", $"type=local,dest={output}", "."],
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var files = Directory.GetFiles(Path.Combine(output, "context"), "*", SearchOption.AllDirectories);
            Assert.Equal(3, files.Count(file => Path.GetFileName(file) == "appsettings.json"));
            foreach (var file in files)
            {
                Assert.True(Path.GetFileName(file) is "appsettings.json" or "Dockerfile" or ".dockerignore");
                Assert.DoesNotContain("synthetic-secret-sentinel", File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}