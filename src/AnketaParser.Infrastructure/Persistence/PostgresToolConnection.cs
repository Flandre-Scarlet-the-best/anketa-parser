using System.Diagnostics;
using System.Globalization;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

/// <summary>Согласует подключения Npgsql и отдельных процессов libpq без передачи секретов в аргументах.</summary>
internal static class PostgresToolConnection
{
    private static readonly HashSet<string> SupportedTargetSessionAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "any",
        "read-write",
        "read-only",
        "primary",
        "standby",
        "prefer-standby",
    };

    internal static bool IsSameDatabase(NpgsqlConnectionStringBuilder source, NpgsqlConnectionStringBuilder target) =>
        HaveSameHostPorts(source, target) &&
        string.Equals(source.Database ?? source.Username, target.Database ?? target.Username, StringComparison.Ordinal);

    private static bool HaveSameHostPorts(NpgsqlConnectionStringBuilder source, NpgsqlConnectionStringBuilder target)
    {
        var sourceHosts = ParseHostPorts(source);
        var targetHosts = ParseHostPorts(target);
        return sourceHosts.Length == targetHosts.Length && sourceHosts.Zip(targetHosts).All(pair =>
            string.Equals(pair.First.Host, pair.Second.Host, StringComparison.OrdinalIgnoreCase) && pair.First.Port == pair.Second.Port);
    }

    private static (string Host, int Port)[] ParseHostPorts(NpgsqlConnectionStringBuilder connection) =>
        string.IsNullOrWhiteSpace(connection.Host)
            ? []
            : connection.Host.Split(',', StringSplitOptions.TrimEntries)
                .Select(host => ParseHostPort(host, connection.Port))
                .ToArray();

    internal static void Validate(NpgsqlConnectionStringBuilder cs)
    {
        // У libpq нет эквивалента системной проверки отзыва .NET и env-параметра sslpassword.
        if (cs.CheckCertificateRevocation || !string.IsNullOrEmpty(cs.SslPassword))
            throw new ArgumentException("Утилиты PostgreSQL не поддерживают заданную проверку отзыва или пароль TLS-ключа через окружение.", nameof(cs));
        if (!string.IsNullOrEmpty(cs.TargetSessionAttributes) &&
            !SupportedTargetSessionAttributes.Contains(cs.TargetSessionAttributes))
            throw new ArgumentException("Утилиты PostgreSQL не поддерживают заданное значение целевого сервера.", nameof(cs));
    }

    internal static void Apply(ProcessStartInfo info, NpgsqlConnectionStringBuilder cs)
    {
        Validate(cs);
        // Только параметры подключения libpq: локализация и настройки сеанса сохраняются.
        foreach (var name in new[]
        {
            "PGHOSTADDR", "PGSERVICE", "PGSERVICEFILE", "PGSYSCONFDIR", "PGPASSFILE", "PGREQUIREAUTH",
            "PGOPTIONS", "PGAPPNAME", "PGREQUIRESSL", "PGSSLCOMPRESSION", "PGSSLCERT", "PGSSLKEY",
            "PGSSLCERTMODE", "PGSSLROOTCERT", "PGSSLCRL", "PGSSLCRLDIR", "PGSSLSNI",
            "PGREQUIREPEER", "PGSSLMINPROTOCOLVERSION", "PGSSLMAXPROTOCOLVERSION", "PGGSSLIB",
            "PGGSSDELEGATION", "PGCLIENTENCODING", "PGTARGETSESSIONATTRS", "PGLOADBALANCEHOSTS",
            "PGMINPROTOCOLVERSION", "PGMAXPROTOCOLVERSION",
        }) info.Environment.Remove(name);
        var configuredHosts = cs.Host;
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredHosts);
        var hostPorts = configuredHosts.Split(',', StringSplitOptions.TrimEntries)
            .Select(host => ParseHostPort(host, cs.Port))
            .ToArray();
        info.Environment["PGHOST"] = string.Join(',', hostPorts.Select(static item => item.Host));
        info.Environment["PGPORT"] = string.Join(',', hostPorts.Select(static item => item.Port.ToString(CultureInfo.InvariantCulture)));
        info.Environment["PGDATABASE"] = cs.Database;
        info.Environment["PGUSER"] = cs.Username;
        info.Environment["PGPASSWORD"] = cs.Password;
        info.Environment["PGCONNECT_TIMEOUT"] = cs.Timeout.ToString(CultureInfo.InvariantCulture);
        info.Environment["PGSSLMODE"] = cs.SslMode switch
        {
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => cs.SslMode.ToString().ToLowerInvariant(),
        };
        info.Environment["PGSSLNEGOTIATION"] = cs.SslNegotiation.ToString().ToLowerInvariant();
        info.Environment["PGGSSENCMODE"] = cs.GssEncryptionMode.ToString().ToLowerInvariant();
        info.Environment["PGCHANNELBINDING"] = cs.ChannelBinding.ToString().ToLowerInvariant();
        info.Environment["PGKRBSRVNAME"] = cs.KerberosServiceName;
        SetIfConfigured(info, "PGSSLROOTCERT", cs.RootCertificate);
        SetIfConfigured(info, "PGSSLCERT", cs.SslCertificate);
        SetIfConfigured(info, "PGSSLKEY", cs.SslKey);
        SetIfConfigured(info, "PGPASSFILE", cs.Passfile);
        SetIfConfigured(info, "PGOPTIONS", cs.Options);
        SetIfConfigured(info, "PGAPPNAME", cs.ApplicationName);
        SetIfConfigured(info, "PGCLIENTENCODING", cs.ClientEncoding);
        SetIfConfigured(info, "PGTARGETSESSIONATTRS", cs.TargetSessionAttributes?.ToLowerInvariant());
        SetIfConfigured(info, "PGREQUIREAUTH", FormatRequireAuthForLibpq(cs.RequireAuth));
        info.Environment["PGLOADBALANCEHOSTS"] = cs.LoadBalanceHosts ? "random" : "disable";
    }

    private static string? FormatRequireAuthForLibpq(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return string.Join(',', value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            var negated = token.StartsWith('!');
            var method = negated ? token[1..] : token;
            var libpqMethod = method.ToLowerInvariant() switch
            {
                "password" => "password",
                "md5" => "md5",
                "gss" => "gss",
                "sspi" => "sspi",
                "scramsha256" => "scram-sha-256",
                "none" => "none",
                _ => throw new ArgumentException("Требование аутентификации не поддерживается утилитами PostgreSQL.", nameof(value)),
            };
            return negated ? "!" + libpqMethod : libpqMethod;
        }));
    }

    private static void SetIfConfigured(ProcessStartInfo info, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) info.Environment[name] = value;
        else info.Environment.Remove(name);
    }

    private static (string Host, int Port) ParseHostPort(string host, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Строка подключения содержит пустой узел PostgreSQL.", nameof(host));

        var address = host;
        var port = defaultPort;
        if (host[0] == '[')
        {
            var closingBracket = host.IndexOf(']');
            if (closingBracket <= 1)
                throw new ArgumentException("Строка подключения содержит некорректный IPv6-узел PostgreSQL.", nameof(host));

            address = host[1..closingBracket];
            var suffix = host[(closingBracket + 1)..];
            if (suffix.Length > 0)
            {
                if (suffix[0] != ':' || !TryParsePort(suffix.AsSpan(1), out port))
                    throw new ArgumentException("Строка подключения содержит некорректный порт PostgreSQL.", nameof(host));
            }
        }
        else if (host.Count(static character => character == ':') == 1)
        {
            var separator = host.LastIndexOf(':');
            if (TryParsePort(host.AsSpan(separator + 1), out var explicitPort))
            {
                if (separator == 0)
                    throw new ArgumentException("Строка подключения содержит пустой узел PostgreSQL.", nameof(host));
                address = host[..separator];
                port = explicitPort;
            }
        }

        return (address, port);
    }

    private static bool TryParsePort(ReadOnlySpan<char> value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}