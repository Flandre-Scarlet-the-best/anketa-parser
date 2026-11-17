using System.Diagnostics;

using AnketaParser.Infrastructure.Persistence;

using Npgsql;

namespace AnketaParser.Tests;

public sealed class PostgresToolConnectionTests
{
    [Fact]
    public void ToolConnectionRemovesInheritedConnectionOverridesAndPreservesLocale()
    {
        var info = new ProcessStartInfo("pg_dump");
        string[] overrides = ["PGSSLROOTCERT", "PGSSLCERT", "PGSSLKEY", "PGPASSFILE", "PGSERVICEFILE", "PGSYSCONFDIR",
            "PGSSLCRL", "PGSSLCRLDIR", "PGSSLCERTMODE", "PGSSLMINPROTOCOLVERSION", "PGSSLMAXPROTOCOLVERSION",
            "PGOPTIONS", "PGAPPNAME", "PGREQUIREAUTH", "PGREQUIREPEER", "PGTARGETSESSIONATTRS",
            "PGSSLSNI", "PGSSLCOMPRESSION", "PGGSSLIB", "PGGSSDELEGATION", "PGMINPROTOCOLVERSION", "PGMAXPROTOCOLVERSION", "PGCLIENTENCODING"];
        foreach (var key in overrides) info.Environment[key] = "чужая настройка";
        info.Environment["PGLOADBALANCEHOSTS"] = "чужая настройка";
        info.Environment["PGLOCALEDIR"] = "локализация";
        PostgresToolConnection.Apply(info, new NpgsqlConnectionStringBuilder { Host = "primary", Database = "product", Username = "backup" });
        Assert.All(overrides, key => Assert.False(info.Environment.ContainsKey(key), key));
        Assert.Equal("локализация", info.Environment["PGLOCALEDIR"]);
        Assert.Equal("disable", info.Environment["PGLOADBALANCEHOSTS"]);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void ToolConnectionTransfersExplicitSessionOptions()
    {
        var info = new ProcessStartInfo("pg_restore");
        PostgresToolConnection.Apply(info, new NpgsqlConnectionStringBuilder
        {
            Host = "primary",
            Options = "-c statement_timeout=10000",
            ApplicationName = "maintenance",
            TargetSessionAttributes = "primary",
            LoadBalanceHosts = true,
            ClientEncoding = "UTF8",
        });
        Assert.Equal("-c statement_timeout=10000", info.Environment["PGOPTIONS"]);
        Assert.Equal("maintenance", info.Environment["PGAPPNAME"]);
        Assert.Equal("primary", info.Environment["PGTARGETSESSIONATTRS"]);
        Assert.Equal("random", info.Environment["PGLOADBALANCEHOSTS"]);
        Assert.Equal("UTF8", info.Environment["PGCLIENTENCODING"]);
    }

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
    public void ToolConnectionTransfersPerHostPortsIncludingBracketedIpv6()
    {
        var info = new ProcessStartInfo("pg_dump");
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "primary:5441,[2001:db8::2]:5442,backup",
            Port = 5432,
        };

        PostgresToolConnection.Apply(info, connection);

        Assert.Equal("primary,2001:db8::2,backup", info.Environment["PGHOST"]);
        Assert.Equal("5441,5442,5432", info.Environment["PGPORT"]);
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