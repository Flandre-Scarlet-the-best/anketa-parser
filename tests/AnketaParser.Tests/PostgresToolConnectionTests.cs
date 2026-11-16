using System.Diagnostics;

using AnketaParser.Infrastructure.Persistence;

using Npgsql;

namespace AnketaParser.Tests;

public sealed class PostgresToolConnectionTests
{
    [Theory]
    [InlineData("primary", 5432, "product", true)]
    [InlineData("PRIMARY", 5432, "product", true)]
    [InlineData("recovery", 5432, "product", false)]
    [InlineData("primary", 5433, "product", false)]
    [InlineData("primary", 5432, "recovery", false)]
    public void DatabaseIdentityIncludesServerPortAndName(string host, int port, string database, bool same)
    {
        var source = new NpgsqlConnectionStringBuilder { Host = "primary", Database = "product" };
        var target = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = database };
        Assert.Equal(same, PostgresToolConnection.IsSameDatabase(source, target));
    }

    [Theory]
    [InlineData(SslMode.Disable, "disable")]
    [InlineData(SslMode.Allow, "allow")]
    [InlineData(SslMode.Prefer, "prefer")]
    [InlineData(SslMode.Require, "require")]
    [InlineData(SslMode.VerifyCA, "verify-ca")]
    [InlineData(SslMode.VerifyFull, "verify-full")]
    public void ToolConnectionPreservesSecurityAndKeepsSecretsOutOfArguments(SslMode mode, string expected)
    {
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = "primary",
            Port = 5433,
            Database = "product",
            Username = "backup",
            Password = "synthetic-secret",
            SslMode = mode,
            RootCertificate = "ca.pem",
            SslCertificate = "client.pem",
            SslKey = "client.key",
            ChannelBinding = ChannelBinding.Require,
            GssEncryptionMode = GssEncryptionMode.Disable,
            SslNegotiation = SslNegotiation.Direct,
            Passfile = "passwords.conf",
            Timeout = 7,
        };
        var info = new ProcessStartInfo("pg_dump");
        info.Environment["PGHOSTADDR"] = "192.0.2.1";
        info.Environment["PGSERVICE"] = "unrelated-service";
        PostgresToolConnection.Apply(info, cs);
        Assert.Equal(expected, info.Environment["PGSSLMODE"]);
        Assert.Equal("ca.pem", info.Environment["PGSSLROOTCERT"]);
        Assert.Equal("client.pem", info.Environment["PGSSLCERT"]);
        Assert.Equal("client.key", info.Environment["PGSSLKEY"]);
        Assert.Equal("require", info.Environment["PGCHANNELBINDING"]);
        Assert.Equal("disable", info.Environment["PGGSSENCMODE"]);
        Assert.Equal("direct", info.Environment["PGSSLNEGOTIATION"]);
        Assert.Equal("passwords.conf", info.Environment["PGPASSFILE"]);
        Assert.Equal("primary", info.Environment["PGHOST"]);
        Assert.Equal("5433", info.Environment["PGPORT"]);
        Assert.Equal("product", info.Environment["PGDATABASE"]);
        Assert.Equal("backup", info.Environment["PGUSER"]);
        Assert.Equal("synthetic-secret", info.Environment["PGPASSWORD"]);
        Assert.Equal("7", info.Environment["PGCONNECT_TIMEOUT"]);
        Assert.False(info.Environment.ContainsKey("PGHOSTADDR"));
        Assert.False(info.Environment.ContainsKey("PGSERVICE"));
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void UnsupportedSecurityRequirementsAreRejectedBeforeProcessStart()
    {
        var info = new ProcessStartInfo("pg_dump");
        Assert.Throws<ArgumentException>(() => PostgresToolConnection.Apply(info,
            new NpgsqlConnectionStringBuilder { CheckCertificateRevocation = true }));
        Assert.Throws<ArgumentException>(() => PostgresToolConnection.Apply(info,
            new NpgsqlConnectionStringBuilder { SslPassword = "synthetic-key-password" }));
        Assert.Throws<ArgumentException>(() => new BackupService(new BackupSettings
        {
            Directory = "unused",
            ConnectionString = "Host=primary;Check Certificate Revocation=true",
        }));
    }
}