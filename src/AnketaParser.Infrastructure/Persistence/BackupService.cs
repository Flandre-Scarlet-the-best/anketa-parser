using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class BackupSettings
{
    public string Directory { get; set; } = string.Empty;
    public string? ExternalDirectory { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public string PgDump { get; set; } = "pg_dump";
    public string PgRestore { get; set; } = "pg_restore";
    public int IntervalSeconds { get; set; } = 86400;
    public int TimeoutSeconds { get; set; } = 1800;
    public int Daily { get; set; } = 7;
    public int Weekly { get; set; } = 4;
    public int Monthly { get; set; } = 3;
    public long MaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public int RestoreJobs { get; set; } = 1;
}

public sealed record BackupState(DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? FailureCategory, string? Archive, DateTimeOffset? LastRestoreVerified);

/// <summary>Архивы публикуются атомарно; проверка структуры и полное восстановление учитываются раздельно.</summary>
public sealed class BackupService
{
    private readonly BackupSettings _settings;
    public BackupService(BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Directory);
        if (settings.TimeoutSeconds < 1 || settings.IntervalSeconds < 1 || settings.Daily < 1 ||
            settings.Weekly < 0 || settings.Monthly < 0 || settings.MaxBytes < 1 || settings.RestoreJobs is < 1 or > 16)
            throw new ArgumentException("Некорректные настройки резервирования.", nameof(settings));
        _settings = settings;
    }

    private string StatePath => Path.Combine(_settings.Directory, "status.json");

    public BackupState Status() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(StatePath)) ?? new(null, null, "invalid_status", null, null)
        : new(null, null, null, null, null);

    public string[] List() => System.IO.Directory.Exists(_settings.Directory)
        ? System.IO.Directory.GetFiles(_settings.Directory, "*.dump").Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.Ordinal).ToArray()
        : [];

    public async Task<string> RunAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_settings.Directory);
        RestrictDirectory(_settings.Directory);
        await using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = Status();
        var name = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".dump";
        var partial = Path.Combine(_settings.Directory, name + ".partial");
        try
        {
            await using (var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(_settings.ConnectionString).Options))
            {
                if (await SchemaMaintenance.StatusAsync(db, ct).ConfigureAwait(false) != "ready")
                    throw new InvalidOperationException("Резервирование требует совместимой прикладной схемы.");
            }
            await RunToolAsync(_settings.PgDump, ["-Fc", "--no-owner", "--no-acl", "--file", partial], _settings.ConnectionString, ct).ConfigureAwait(false);
            RestrictFile(partial);
            await RunToolAsync(_settings.PgRestore, ["--list", partial], null, ct).ConfigureAwait(false);
            // Чтение всех блоков архива обнаруживает повреждения данных, которых не видно в оглавлении.
            await RunToolAsync(_settings.PgRestore, ["--file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", partial], null, ct).ConfigureAwait(false);
            var size = new FileInfo(partial).Length;
            if (size <= 0 || size > _settings.MaxBytes) throw new IOException("Архив превышает лимит хранилища.");
            var protectedBytes = List().Select(x => new FileInfo(Path.Combine(_settings.Directory, x)))
                .Where(x => File.Exists(x.FullName + ".verified")).Sum(x => x.Length);
            if (protectedBytes + size > _settings.MaxBytes) throw new IOException("Проверенные копии и новый архив превышают лимит.");
            var destination = Path.Combine(_settings.Directory, name);
            var digest = await DigestAsync(partial, ct).ConfigureAwait(false);
            File.Move(partial, destination);
            await File.WriteAllTextAsync(destination + ".sha256", digest, ct).ConfigureAwait(false);
            RestrictFile(destination + ".sha256");
            if (!string.IsNullOrWhiteSpace(_settings.ExternalDirectory))
            {
                System.IO.Directory.CreateDirectory(_settings.ExternalDirectory);
                RestrictDirectory(_settings.ExternalDirectory);
                var external = Path.Combine(_settings.ExternalDirectory, name);
                File.Copy(destination, external + ".partial");
                RestrictFile(external + ".partial");
                if (await DigestAsync(external + ".partial", ct).ConfigureAwait(false) != digest) throw new IOException("Нарушена целостность внешней копии.");
                File.Move(external + ".partial", external);
                await File.WriteAllTextAsync(external + ".sha256", digest, ct).ConfigureAwait(false);
                RestrictFile(external + ".sha256");
            }
            WriteState(state with { LastSuccess = DateTimeOffset.UtcNow, FailureCategory = null, Archive = name });
            RotateCore();
            return name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            WriteState(state with { LastFailure = DateTimeOffset.UtcNow, FailureCategory = exception is TimeoutException ? "timeout" : "backup_failed" });
            throw new InvalidOperationException("Резервирование завершилось отказом; последняя успешная копия сохранена.");
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public void Rotate()
    {
        using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RotateCore();
    }

    private void RotateCore()
    {
        var files = List().Select(name => new FileInfo(Path.Combine(_settings.Directory, name))).OrderByDescending(x => x.LastWriteTimeUtc).ThenByDescending(x => x.Name, StringComparer.Ordinal).ToArray();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.GroupBy(x => x.LastWriteTimeUtc.Date).Take(_settings.Daily).Select(x => x.First())
            .Concat(files.GroupBy(x => System.Globalization.ISOWeek.GetYear(x.LastWriteTimeUtc).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + System.Globalization.ISOWeek.GetWeekOfYear(x.LastWriteTimeUtc).ToString(System.Globalization.CultureInfo.InvariantCulture)).Take(_settings.Weekly).Select(x => x.First()))
            .Concat(files.GroupBy(x => (x.LastWriteTimeUtc.Year, x.LastWriteTimeUtc.Month)).Take(_settings.Monthly).Select(x => x.First())))
            keep.Add(file.Name);
        var state = Status();
        if (state.Archive is not null) keep.Add(state.Archive);
        foreach (var file in files.Where(x => File.Exists(x.FullName + ".verified"))) keep.Add(file.Name);
        foreach (var file in files.Where(x => !keep.Contains(x.Name))) DeleteArchive(file);
        var remaining = files.Where(x => x.Exists).ToList();
        long total = remaining.Sum(x => x.Length);
        foreach (var file in remaining.AsEnumerable().Reverse())
        {
            if (total <= _settings.MaxBytes) break;
            if (file.Name == state.Archive || File.Exists(file.FullName + ".verified")) continue;
            total -= file.Length; DeleteArchive(file);
        }
        if (total > _settings.MaxBytes) throw new IOException("Лимит занят защищёнными копиями; требуется увеличение ёмкости или ручной перенос.");
    }

    private static void DeleteArchive(FileInfo file)
    {
        file.Delete();
        File.Delete(file.FullName + ".sha256");
    }

    public async Task VerifyAsync(string name, CancellationToken ct)
    {
        var path = ArchivePath(name);
        if (!File.Exists(path + ".sha256") || await DigestAsync(path, ct).ConfigureAwait(false) != await File.ReadAllTextAsync(path + ".sha256", ct).ConfigureAwait(false))
            throw new InvalidOperationException("Контрольная сумма архива не совпадает.");
        await RunToolAsync(_settings.PgRestore, ["--file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", path], null, ct).ConfigureAwait(false);
    }

    /// <summary>Восстанавливает только в явно указанную другую пустую БД, без --clean и без удаления данных.</summary>
    public async Task RestoreAsync(string name, string targetConnectionString, CancellationToken ct)
    {
        await using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await VerifyAsync(name, ct).ConfigureAwait(false);
        var source = new NpgsqlConnectionStringBuilder(_settings.ConnectionString);
        var target = new NpgsqlConnectionStringBuilder(targetConnectionString);
        if (source.Database == target.Database) throw new InvalidOperationException("Для восстановления требуется другая БД.");
        await using var connection = new NpgsqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND c.relkind IN ('r','p','v','m','S')", connection);
        if ((long)(await check.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0) throw new InvalidOperationException("Целевая БД не пуста.");
        var args = new List<string> { "--exit-on-error", "--no-owner", "--no-acl" };
        if (_settings.RestoreJobs == 1) args.Add("--single-transaction");
        else { args.Add("--jobs"); args.Add(_settings.RestoreJobs.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.Add("--dbname"); args.Add(target.Database!); args.Add(ArchivePath(name));
        await RunToolAsync(_settings.PgRestore, args, targetConnectionString, ct).ConfigureAwait(false);
        await using var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(targetConnectionString).Options);
        if (await SchemaMaintenance.StatusAsync(db, ct).ConfigureAwait(false) != "ready") throw new InvalidOperationException("Восстановлена несовместимая схема.");
        var state = Status();
        await File.WriteAllTextAsync(ArchivePath(name) + ".verified", DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        RestrictFile(ArchivePath(name) + ".verified");
        WriteState(state with { LastRestoreVerified = DateTimeOffset.UtcNow });
    }

    private string ArchivePath(string name)
    {
        if (Path.GetFileName(name) != name || !name.EndsWith(".dump", StringComparison.Ordinal)) throw new ArgumentException("Требуется имя архива из списка.", nameof(name));
        return Path.Combine(_settings.Directory, name);
    }

    private void WriteState(BackupState state)
    {
        File.WriteAllText(StatePath + ".partial", JsonSerializer.Serialize(state));
        RestrictFile(StatePath + ".partial");
        File.Move(StatePath + ".partial", StatePath, overwrite: true);
    }

    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private async Task RunToolAsync(string executable, IReadOnlyList<string> arguments, string? connectionString, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (connectionString is not null)
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString);
            info.Environment["PGHOST"] = cs.Host; info.Environment["PGPORT"] = cs.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["PGDATABASE"] = cs.Database; info.Environment["PGUSER"] = cs.Username; info.Environment["PGPASSWORD"] = cs.Password;
            info.Environment["PGCONNECT_TIMEOUT"] = "10";
        }
        using var process = new Process { StartInfo = info };
        process.Start();
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(output, errors).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Превышено время операции PostgreSQL.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Утилита PostgreSQL завершилась отказом.");
    }
}