using AnketaParser.Infrastructure.Configuration;

namespace AnketaParser.Tests;

public sealed class ConnectionStringDescriptorTests
{
    private const string ConnectionString =
        "Host=postgres;Port=5432;Database=anketa_parser;Username=anketa_parser;Password=super-secret";

    [Fact]
    public void DescribeReturnsTargetWithoutCredentials()
    {
        var description = ConnectionStringDescriptor.Describe(ConnectionString);

        Assert.Equal("postgres:5432/anketa_parser", description);
        Assert.DoesNotContain("super-secret", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DescribeReportsNotConfiguredForEmptyValue(string? value)
    {
        Assert.Equal("(not configured)", ConnectionStringDescriptor.Describe(value));
    }

    [Fact]
    public void DescribeReportsInvalidForUnparsableValue()
    {
        Assert.Equal("(invalid)", ConnectionStringDescriptor.Describe("definitely not a connection string"));
    }

    [Fact]
    public void RedactRemovesPassword()
    {
        var redacted = ConnectionStringDescriptor.Redact(ConnectionString);

        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
        Assert.Contains("anketa_parser", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactKeepsPasswordlessConnectionStringUsable()
    {
        const string withoutPassword = "Host=postgres;Database=anketa_parser;Username=anketa_parser";

        var redacted = ConnectionStringDescriptor.Redact(withoutPassword);

        Assert.DoesNotContain("***", redacted, StringComparison.Ordinal);
        Assert.Equal("postgres", new Npgsql.NpgsqlConnectionStringBuilder(redacted).Host);
    }
}