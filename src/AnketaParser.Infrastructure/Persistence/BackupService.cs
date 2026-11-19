using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class BackupSettings
{
    public const string SectionName = "Backup";

    public string Directory { get; set; } = string.Empty;
    public string? ExternalDirectory { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public string? Password { get; set; }
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

public sealed record BackupState(DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? FailureCategory, string? Archive, DateTimeOffset? LastRestoreVerified)
{
    public long? ArchiveLength { get; init; }
    public string? ArchiveDigest { get; init; }
}

internal sealed record RestoreVerification(string Archive, long Length, string Digest, DateTimeOffset VerifiedAt);

/// <summary>Что удалось записать о выполненном восстановлении в служебный статус резервирования.</summary>
public enum RestoreStatusRecord
{
    /// <summary>Статус текущей копии обновлён отметкой проверки восстановлением.</summary>
    Updated,

    /// <summary>Архив не является текущим: подтверждение сохранено только отметкой рядом с архивом.</summary>
    NotCurrentArchive,

    /// <summary>Повреждённый статус сохранён отдельно, вместо него записан корректный.</summary>
    RepairedFromCorrupt,

    /// <summary>Статус не прочитан или не записан; факт восстановления подтверждён только отметкой архива.</summary>
    NotRecorded,
}

/// <summary>Раздельный результат восстановления: сам факт и запись служебного статуса.</summary>
public sealed record RestoreOutcome(string Archive, DateTimeOffset VerifiedAt, RestoreStatusRecord StatusRecord);

/// <summary>Данные восстановлены и подтверждены, но служебная отметка проверки не записана.</summary>
public sealed class RestoreCompletionException : InvalidOperationException
{
    public RestoreCompletionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RestoreTargetException : InvalidOperationException
{
    public RestoreTargetException() : base("Восстановление разрешено только в другую пустую БД без заранее созданных прикладных схем и пользовательских объектов.") { }
}

/// <summary>Архивы публикуются атомарно; проверка структуры и полное восстановление учитываются раздельно.</summary>
public sealed class BackupService
{
    private readonly BackupSettings _settings;
    private readonly Func<string, string> _readState;

    public BackupService(BackupSettings settings) : this(settings, File.ReadAllText) { }

    internal BackupService(BackupSettings settings, Func<string, string> readState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Directory);
        if (settings.TimeoutSeconds < 1 || settings.IntervalSeconds < 1 || settings.Daily < 1 ||
            settings.Weekly < 0 || settings.Monthly < 0 || settings.MaxBytes < 1 || settings.RestoreJobs is < 1 or > 16)
            throw new ArgumentException("Некорректные настройки резервирования.", nameof(settings));
        _settings = settings;
        _readState = readState;
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            PostgresToolConnection.Validate(new NpgsqlConnectionStringBuilder(settings.ConnectionString));
    }

    private string StatePath => Path.Combine(_settings.Directory, "status.json");

    public BackupState Status() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<BackupState>(_readState(StatePath)) ?? new(null, null, "invalid_status", null, null)
        : new(null, null, null, null, null);

    public string[] List() => System.IO.Directory.Exists(_settings.Directory)
        ? System.IO.Directory.GetFiles(_settings.Directory, "*.dump").Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.Ordinal).ToArray()
        : [];

    public async Task<string> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        var operation = deadline.Token;
        System.IO.Directory.CreateDirectory(_settings.Directory);
        RestrictDirectory(_settings.Directory);
        await using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = new BackupState(null, null, null, null, null);
        var stateCanBeWritten = false;
        var stateWriteFailed = false;
        var name = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".dump";
        var partial = Path.Combine(_settings.Directory, name + ".partial");
        var destination = Path.Combine(_settings.Directory, name);
        var external = string.IsNullOrWhiteSpace(_settings.ExternalDirectory) ? null : Path.Combine(_settings.ExternalDirectory, name);
        var published = false;
        try
        {
            try
            {
                state = Status();
                stateCanBeWritten = true;
            }
            catch (JsonException)
            {
                stateCanBeWritten = false;
                PreserveCorruptState();
                state = new BackupState(null, null, "invalid_status", null, null);
                stateCanBeWritten = true;
            }

            await using (var db = ProductDbContext.Create(_settings.ConnectionString))
            {
                if (await SchemaMaintenance.StatusAsync(db, operation).ConfigureAwait(false) != "ready")
                    throw new InvalidOperationException("Резервирование требует совместимой прикладной схемы.");
            }
            var dumpArguments = DumpArguments();
            dumpArguments.AddRange(["--file", partial]);
            await RunToolAsync(_settings.PgDump, dumpArguments, _settings.ConnectionString, operation).ConfigureAwait(false);
            RestrictFile(partial);
            await RunToolAsync(_settings.PgRestore, ["--list", partial], null, operation).ConfigureAwait(false);
            // Чтение всех блоков архива обнаруживает повреждения данных, которых не видно в оглавлении.
            await RunToolAsync(_settings.PgRestore, ["--file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", partial], null, operation).ConfigureAwait(false);
            var size = new FileInfo(partial).Length;
            if (size <= 0 || size > _settings.MaxBytes) throw new IOException("Архив превышает лимит хранилища.");
            // Ограниченная ротация защищает только опубликованную и последнюю подтверждённую копию,
            // поэтому проверять лимит нужно по этому минимуму, а не по всем когда-либо проверенным архивам.
            var protectedBytes = ProtectedArchives(state, ManagedArchives())
                .Sum(archive => new FileInfo(Path.Combine(_settings.Directory, archive)).Length);
            if (protectedBytes + size > _settings.MaxBytes) throw new IOException("Защищённые копии и новый архив превышают лимит.");
            var digest = await DigestAsync(partial, operation).ConfigureAwait(false);
            if (external is not null)
            {
                System.IO.Directory.CreateDirectory(_settings.ExternalDirectory!);
                RestrictDirectory(_settings.ExternalDirectory!);
                await using (var input = File.OpenRead(partial))
                await using (var output = new FileStream(external + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await CopyArchiveAsync(input, output, operation).ConfigureAwait(false);
                RestrictFile(external + ".partial");
                if (await DigestAsync(external + ".partial", operation).ConfigureAwait(false) != digest) throw new IOException("Нарушена целостность внешней копии.");
                await PublishArchiveAsync(external + ".partial", external, digest, operation).ConfigureAwait(false);
            }
            await PublishArchiveAsync(partial, destination, digest, operation).ConfigureAwait(false);
            var next = state with { LastSuccess = DateTimeOffset.UtcNow, FailureCategory = null, Archive = name, ArchiveLength = size, ArchiveDigest = digest, LastRestoreVerified = null };
            WriteState(next);
            state = next;
            published = true;
            operation.ThrowIfCancellationRequested();
            RotateCore(operation);
            operation.ThrowIfCancellationRequested();
            return name;
        }
        catch (Exception exception)
        {
            var category = ct.IsCancellationRequested ? "cancelled" : exception is TimeoutException || deadline.IsCancellationRequested ? "timeout" : published ? "rotation_failed" : "backup_failed";
            if (stateCanBeWritten)
            {
                try
                {
                    WriteState(state with { LastFailure = DateTimeOffset.UtcNow, FailureCategory = category });
                }
                catch (Exception writeStateException) when (writeStateException is IOException or UnauthorizedAccessException)
                {
                    stateWriteFailed = true;
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(published
                ? "Копия опубликована, но обслуживание хранилища завершилось отказом; проверьте backup-status."
                : stateWriteFailed || !stateCanBeWritten
                    ? "Резервирование завершилось отказом; состояние не обновлено, проверьте последнюю успешную копию вручную."
                    : "Резервирование завершилось отказом; последняя успешная копия сохранена.");
        }
        finally
        {
            if (!published)
            {
                RemoveUnpublished(destination);
                if (external is not null) RemoveUnpublished(external);
            }
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static List<string> DumpArguments()
    {
        // Положительная граница архива: схемы продукта целиком. История миграций принадлежит product,
        // поэтому public остаётся вне дампа, и посторонние таблицы, функции и типы туда не попадают.
        var arguments = new List<string> { "-Fc", "--no-owner", "--no-acl", "--no-large-objects" };
        foreach (var schema in new[] { ProductDbContext.Schema, ProductDbContext.PrivateSchema })
        {
            arguments.Add("--schema");
            arguments.Add(schema);
        }
        return arguments;
    }

    internal static Task CopyArchiveAsync(Stream source, Stream destination, CancellationToken ct) => source.CopyToAsync(destination, ct);

    private static async Task PublishArchiveAsync(string partial, string destination, string digest, CancellationToken ct)
    {
        await File.WriteAllTextAsync(destination + ".sha256.partial", digest, ct).ConfigureAwait(false);
        RestrictFile(destination + ".sha256.partial");
        ct.ThrowIfCancellationRequested();
        File.Move(destination + ".sha256.partial", destination + ".sha256");
        // Имя .dump становится видимым только после готовности контрольной суммы.
        File.Move(partial, destination);
    }

    private static void RemoveUnpublished(string path)
    {
        foreach (var suffix in new[] { "", ".partial", ".sha256", ".sha256.partial" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    public void Rotate()
    {
        using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RotateCore();
    }

    private void RotateCore(CancellationToken ct = default)
    {
        var files = List().Select(name => new FileInfo(Path.Combine(_settings.Directory, name)))
            .OrderByDescending(x => x.LastWriteTimeUtc).ThenByDescending(x => x.Name, StringComparer.Ordinal).ToArray();
        var managed = files.Where(x => IsManagedArchive(x.Name) && File.Exists(x.FullName + ".sha256")).ToArray();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in managed.GroupBy(x => x.LastWriteTimeUtc.Date).Take(_settings.Daily).Select(x => x.First())
            .Concat(managed.GroupBy(x => System.Globalization.ISOWeek.GetYear(x.LastWriteTimeUtc).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + System.Globalization.ISOWeek.GetWeekOfYear(x.LastWriteTimeUtc).ToString(System.Globalization.CultureInfo.InvariantCulture)).Take(_settings.Weekly).Select(x => x.First()))
            .Concat(managed.GroupBy(x => (x.LastWriteTimeUtc.Year, x.LastWriteTimeUtc.Month)).Take(_settings.Monthly).Select(x => x.First())))
            keep.Add(file.Name);
        var state = Status();
        // Бессрочная защита всех проверенных копий отменяла бы календарную ротацию и лимит ёмкости.
        var protectedNames = ProtectedArchives(state, managed);
        keep.UnionWith(protectedNames);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in managed.Where(x => !keep.Contains(x.Name)))
        {
            ct.ThrowIfCancellationRequested();
            DeleteArchive(file);
            removed.Add(file.Name);
        }
        var remaining = files.Where(x => !removed.Contains(x.Name)).ToList();
        long total = remaining.Sum(x => x.Length);
        // Самые старые управляемые архивы, включая вышедшие за календарные категории, освобождают лимит
        // до защищённого минимума; последняя пригодная подтверждённая копия не удаляется.
        foreach (var file in remaining.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (total <= _settings.MaxBytes) break;
            if (!managed.Contains(file) || protectedNames.Contains(file.Name)) continue;
            total -= file.Length; DeleteArchive(file);
        }
        if (total > _settings.MaxBytes) throw new IOException("Лимит занят защищёнными или сторонними копиями; требуется увеличение ёмкости или ручной перенос.");
    }

    /// <summary>Управляемые архивы в порядке от новых к старым: имя продукта и опубликованная сумма.</summary>
    private FileInfo[] ManagedArchives() =>
        List().Select(name => new FileInfo(Path.Combine(_settings.Directory, name)))
            .Where(x => IsManagedArchive(x.Name) && File.Exists(x.FullName + ".sha256"))
            .OrderByDescending(x => x.LastWriteTimeUtc).ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Копии, которые нельзя удалять: опубликованная и последняя достоверно подтверждённая восстановлением.</summary>
    private static HashSet<string> ProtectedArchives(BackupState state, IReadOnlyList<FileInfo> managed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (state.Archive is not null && managed.Any(x => string.Equals(x.Name, state.Archive, StringComparison.Ordinal)))
            names.Add(state.Archive);
        foreach (var file in managed)
        {
            if (!IsVerifiedByRestore(file)) continue;
            names.Add(file.Name);
            break;
        }
        return names;
    }

    /// <summary>Отметка .verified защищает копию только когда она совпадает с самим архивом и его суммой.</summary>
    private static bool IsVerifiedByRestore(FileInfo archive)
    {
        try
        {
            var digest = ReadMetadata(archive.FullName + ".sha256");
            var verification = JsonSerializer.Deserialize<RestoreVerification>(ReadMetadata(archive.FullName + ".verified"));
            return verification is not null && string.Equals(verification.Archive, archive.Name, StringComparison.Ordinal) &&
                verification.Length == archive.Length && string.Equals(verification.Digest, digest, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private static void DeleteArchive(FileInfo file)
    {
        file.Delete();
        File.Delete(file.FullName + ".sha256");
        // Отметка восстановления относится только к удалённой копии: осиротевшая защищала бы несуществующий архив.
        File.Delete(file.FullName + ".verified");
    }

    /// <summary>Проверяет архив, удерживая блокировку каталога: параллельная ротация не удалит проверяемый файл.</summary>
    public async Task VerifyAsync(string name, CancellationToken ct)
    {
        using var gate = AcquireGate();
        await VerifyCoreAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>Сериализует операции над каталогом; вызов внутри уже удерживаемой блокировки недопустим.</summary>
    private FileStream AcquireGate()
    {
        try
        {
            return new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Каталог резервирования занят другой операцией; повторите попытку после её завершения.", exception);
        }
    }

    private async Task VerifyCoreAsync(string name, CancellationToken ct)
    {
        var path = ArchivePath(name);
        if (!File.Exists(path + ".sha256") || await DigestAsync(path, ct).ConfigureAwait(false) != await File.ReadAllTextAsync(path + ".sha256", ct).ConfigureAwait(false))
            throw new InvalidOperationException("Контрольная сумма архива не совпадает.");
        await RunToolAsync(_settings.PgRestore, ["--file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", path], null, ct).ConfigureAwait(false);
    }

    /// <summary>Восстанавливает только в явно указанную другую пустую БД, без --clean и без удаления данных.</summary>
    public async Task<RestoreOutcome> RestoreAsync(string name, string targetConnectionString, CancellationToken ct)
    {
        await using var gate = new FileStream(Path.Combine(_settings.Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var source = new NpgsqlConnectionStringBuilder(_settings.ConnectionString);
        var target = new NpgsqlConnectionStringBuilder(targetConnectionString);
        if (PostgresToolConnection.IsSameDatabase(source, target)) throw new RestoreTargetException();
        await using var connection = new NpgsqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var check = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname <> 'public' AND nspname <> 'information_schema' AND nspname !~ '^pg_')
              OR EXISTS (SELECT 1 FROM pg_depend d JOIN pg_namespace n ON d.refobjid=n.oid
                WHERE d.refclassid='pg_namespace'::regclass AND n.nspname='public')
              OR EXISTS (SELECT 1 FROM pg_extension WHERE extname<>'plpgsql')
            """, connection);
        if ((bool)(await check.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            throw new RestoreTargetException();
        await VerifyCoreAsync(name, ct).ConfigureAwait(false);
        var args = new List<string> { "--exit-on-error", "--no-owner", "--no-acl" };
        if (_settings.RestoreJobs == 1) args.Add("--single-transaction");
        else { args.Add("--jobs"); args.Add(_settings.RestoreJobs.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.Add("--dbname"); args.Add(PostgresToolConnection.FormatDatabaseArgument(target.Database ?? target.Username ?? Environment.UserName)); args.Add(ArchivePath(name));
        await RunToolAsync(_settings.PgRestore, args, targetConnectionString, ct).ConfigureAwait(false);
        await using var db = ProductDbContext.Create(targetConnectionString);
        if (await SchemaMaintenance.StatusAsync(db, ct).ConfigureAwait(false) != "ready") throw new InvalidOperationException("Восстановлена несовместимая схема.");
        // Архив применён и схема совместима. Подтверждение восстановления фиксируется независимо
        // от служебного status.json: его повреждение не отменяет уже выполненное восстановление.
        var verifiedAt = DateTimeOffset.UtcNow;
        var verification = await PublishRestoreVerificationAsync(name, verifiedAt).ConfigureAwait(false);
        return new RestoreOutcome(verification.Archive, verifiedAt, RecordRestoreVerification(verification));
    }

    /// <summary>
    /// Публикует отметку полного восстановления так же атомарно, как и сам архив.
    /// Отмена здесь не применяется: фиксация уже выполненного восстановления иначе была бы потеряна,
    /// команда сообщила бы об остановке, а повторный restore-empty отклонил бы заполненную цель.
    /// </summary>
    private async Task<RestoreVerification> PublishRestoreVerificationAsync(string name, DateTimeOffset verifiedAt)
    {
        var path = ArchivePath(name);
        RestoreVerification verification;
        try
        {
            verification = new RestoreVerification(name, new FileInfo(path).Length, ReadMetadata(path + ".sha256"), verifiedAt);
            await File.WriteAllTextAsync(path + ".verified.partial", JsonSerializer.Serialize(verification), CancellationToken.None).ConfigureAwait(false);
            RestrictFile(path + ".verified.partial");
            File.Move(path + ".verified.partial", path + ".verified", overwrite: true);
            RestrictFile(path + ".verified");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RestoreCompletionException(
                "Архив применён к целевой БД и схема совместима, но отметка проверки восстановления не записана; устраните причину и повторите восстановление в другую пустую БД.",
                exception);
        }
        return verification;
    }

    /// <summary>Отдельно от факта восстановления обновляет служебный статус и сообщает, что именно удалось записать.</summary>
    private RestoreStatusRecord RecordRestoreVerification(RestoreVerification verification)
    {
        BackupState state;
        try
        {
            state = Status();
        }
        catch (JsonException)
        {
            try
            {
                PreserveCorruptState();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return RestoreStatusRecord.NotRecorded;
            }
            // Прежний архив неизвестен: восстанавливаем корректный статус, не выдумывая LastSuccess.
            return TryWriteState(new BackupState(null, DateTimeOffset.UtcNow, "invalid_status", null, null))
                ? RestoreStatusRecord.RepairedFromCorrupt
                : RestoreStatusRecord.NotRecorded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Статус не читается: перезапись скрыла бы неизвестное состояние.
            return RestoreStatusRecord.NotRecorded;
        }
        if (state.Archive != verification.Archive) return RestoreStatusRecord.NotCurrentArchive;
        return TryWriteState(state with { LastRestoreVerified = verification.VerifiedAt, ArchiveLength = verification.Length, ArchiveDigest = verification.Digest })
            ? RestoreStatusRecord.Updated
            : RestoreStatusRecord.NotRecorded;
    }

    private bool TryWriteState(BackupState state)
    {
        try
        {
            WriteState(state);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void PreserveCorruptState()
    {
        var preserved = Path.Combine(_settings.Directory, "status-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(StatePath, preserved);
        RestrictFile(preserved);
    }

    internal bool IsCurrentArchiveAvailable(BackupState state)
    {
        try
        {
            if (state.Archive is null || state.ArchiveLength is not > 0 || state.ArchiveDigest?.Length != 64) return false;
            var path = ArchivePath(state.Archive);
            using var archive = File.OpenRead(path);
            if (archive.Length != state.ArchiveLength || ReadMetadata(path + ".sha256") != state.ArchiveDigest) return false;
            var verification = JsonSerializer.Deserialize<RestoreVerification>(ReadMetadata(path + ".verified"));
            return verification is not null && verification.Archive == state.Archive && verification.Length == archive.Length &&
                verification.Digest == state.ArchiveDigest && verification.VerifiedAt == state.LastRestoreVerified;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private static string ReadMetadata(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[4096];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length == buffer.Length) throw new IOException("Метаданные копии превышают допустимый размер.");
        return new string(buffer, 0, length);
    }

    public static bool IsArchiveName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(['/', '\\']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        Path.GetFileName(name) == name && name.EndsWith(".dump", StringComparison.Ordinal);

    // Формат имени совпадает с RunAsync: UTC-метка с семью долями секунды и UUID без дефисов.
    private static bool IsManagedArchive(string name) => name.Length == 60 && name[22] == '-' &&
        DateTime.TryParseExact(name[..22], "yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _) &&
        Guid.TryParseExact(name.AsSpan(23, 32), "N", out _) && name.EndsWith(".dump", StringComparison.Ordinal);

    private string ArchivePath(string name)
    {
        if (!IsArchiveName(name)) throw new ArgumentException("Требуется имя архива из списка.", nameof(name));
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

    /// <summary>
    /// Параметры запуска утилиты PostgreSQL. Вывод забирается перенаправленными потоками,
    /// поэтому видимое окно консоли не нужно: <c>CreateNoWindow</c> убирает его мерцание
    /// при каждой операции резервного копирования и восстановления.
    /// </summary>
    /// <param name="executable">Путь к утилите.</param>
    /// <param name="arguments">Аргументы командной строки.</param>
    /// <param name="connectionString">Строка подключения либо <c>null</c>, если утилите она не нужна.</param>
    /// <returns>Параметры запуска без окна консоли и с перенаправленными потоками.</returns>
    internal static ProcessStartInfo CreateToolStartInfo(
        string executable, IReadOnlyList<string> arguments, string? connectionString)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (connectionString is not null)
        {
            PostgresToolConnection.Apply(info, new NpgsqlConnectionStringBuilder(connectionString));
        }

        return info;
    }

    private async Task RunToolAsync(string executable, IReadOnlyList<string> arguments, string? connectionString, CancellationToken ct)
    {
        var info = CreateToolStartInfo(executable, arguments, connectionString);
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        ct.ThrowIfCancellationRequested();
        process.Start();
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(output, errors).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Процесс уже завершён. */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Отмена чтения ожидаема после остановки процесса. */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Превышено время операции PostgreSQL.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Утилита PostgreSQL завершилась отказом.");
    }
}