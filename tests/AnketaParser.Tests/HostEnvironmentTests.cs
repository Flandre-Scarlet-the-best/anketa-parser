using AnketaParser.Host;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AnketaParser.Tests;

// Только тесты, меняющие окружение процесса, требуют исключительного исполнения.
[CollectionDefinition("Окружение процесса", DisableParallelization = true)]
public sealed class ProcessEnvironmentDefinition;

[Collection("Окружение процесса")]
public sealed class HostEnvironmentTests
{
    [Fact]
    public async Task UnexpectedHostFailureKeepsInternalExitCodeWithoutRawPath()
    {
        var original = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var code = await HostApplication.RunAsync([], TestContext.Current.CancellationToken, "\0host-secret-sentinel");
            Assert.Equal(1, code);
            Assert.Contains("ArgumentException", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("host-secret-sentinel", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Theory]
    [InlineData("37", 37)]
    [InlineData("0", 0)]
    [InlineData("not-an-int", 0)]
    public void ShutdownTimeoutUsesValidatedEnvironmentValue(string value, int expectedSeconds)
    {
        const string variable = "ANKETAPARSER_Host__ShutdownTimeoutSeconds";
        var original = Environment.GetEnvironmentVariable(variable);
        using var root = new Support.TemporaryContentRoot("""{ "Host": { "ShutdownTimeoutSeconds": 9 } }""");
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            if (expectedSeconds == 0)
            {
                Assert.Throws<OptionsValidationException>(() => HostApplication.CreateHost([], root.Path));
            }
            else
            {
                using var host = HostApplication.CreateHost([], root.Path);
                Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
                    host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }

        Assert.Equal(original, Environment.GetEnvironmentVariable(variable));
    }
}