using System.Diagnostics;
using System.Globalization;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

/// <summary>Согласует подключения Npgsql и отдельных процессов libpq без передачи секретов в аргументах.</summary>
internal static class PostgresToolConnection
{
    internal static bool IsSameDatabase(NpgsqlConnectionStringBuilder source, NpgsqlConnectionStringBuilder target) =>
        string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase) && source.Port == target.Port &&
        string.Equals(source.Database ?? source.Username, target.Database ?? target.Username, StringComparison.Ordinal);

    internal static void Validate(NpgsqlConnectionStringBuilder cs)
    {
        // У libpq нет эквивалента системной проверки отзыва .NET и env-параметра sslpassword.
        if (cs.CheckCertificateRevocation || !string.IsNullOrEmpty(cs.SslPassword))
            throw new ArgumentException("Утилиты PostgreSQL не поддерживают заданную проверку отзыва или пароль TLS-ключа через окружение.", nameof(cs));
    }

    internal static void Apply(ProcessStartInfo info, NpgsqlConnectionStringBuilder cs)
    {
        Validate(cs);
        info.Environment.Remove("PGHOSTADDR");
        info.Environment.Remove("PGSERVICE");
        info.Environment.Remove("PGSERVICEFILE");
        info.Environment["PGHOST"] = cs.Host;
        info.Environment["PGPORT"] = cs.Port.ToString(CultureInfo.InvariantCulture);
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
    }

    private static void SetIfConfigured(ProcessStartInfo info, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) info.Environment[name] = value;
    }
}