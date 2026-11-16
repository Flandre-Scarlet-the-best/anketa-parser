using System.Security.Cryptography;
using System.Text.Json;

using AnketaParser.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;

namespace AnketaParser.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "anketa-backup-unit-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("valid", Core.Diagnostics.ProbeStatus.Healthy)]
    [InlineData("missing_archive", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("changed_length", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("missing_checksum", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("changed_checksum", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("missing_verification", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("other_archive", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("old_verification", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("old_backup", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("latest_failure", Core.Diagnostics.ProbeStatus.Skipped)]
    [InlineData("invalid_status", Core.Diagnostics.ProbeStatus.Skipped)]
    public async Task ReadinessRequiresAvailableArchiveAndMatchingRestoreEvidence(string scenario, Core.Diagnostics.ProbeStatus expected)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "latest.dump");
        File.WriteAllText(path, "синтетический архив");
        var now = DateTimeOffset.UtcNow;
        var verifiedAt = scenario == "old_verification" ? now.AddDays(-3) : now;
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var state = new BackupState(scenario == "old_backup" ? now.AddDays(-3) : now,
            scenario == "latest_failure" ? now.AddSeconds(1) : null, null, "latest.dump", verifiedAt)
        { ArchiveLength = new FileInfo(path).Length, ArchiveDigest = digest };
        File.WriteAllText(path + ".sha256", digest);
        File.WriteAllText(path + ".verified", JsonSerializer.Serialize(new RestoreVerification(
            scenario == "other_archive" ? "older.dump" : "latest.dump", state.ArchiveLength.Value, digest, verifiedAt)));
        File.WriteAllText(Path.Combine(_directory, "status.json"), scenario == "invalid_status" ? "{неверный JSON}" : JsonSerializer.Serialize(state));
        if (scenario == "missing_archive") File.Delete(path);
        if (scenario == "changed_length") File.AppendAllText(path, "изменение");
        if (scenario == "missing_checksum") File.Delete(path + ".sha256");
        if (scenario == "changed_checksum") File.WriteAllText(path + ".sha256", new string('0', 64));
        if (scenario == "missing_verification") File.Delete(path + ".verified");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = _directory,
        }).Build();
        var result = await new BackupReadinessProbe(configuration).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(_directory, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalCopyObservesCancellationDuringWrite()
    {
        using var cancellation = new CancellationTokenSource();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "copy.partial");
        await File.WriteAllBytesAsync(path, new byte[100_000], TestContext.Current.CancellationToken);
        await using var source = File.OpenRead(path);
        await using var destination = new BlockingWriteStream();
        var copy = BackupService.CopyArchiveAsync(source, destination, cancellation.Token);
        await destination.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, destination.Length);
    }

    private sealed class BlockingWriteStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    [Fact]
    public async Task FreshDatesWithoutArchiveCannotAdvertiseHealthy()
    {
        Directory.CreateDirectory(_directory);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(_directory, "status.json"), JsonSerializer.Serialize(new BackupState(now, null, null, "missing.dump", now)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = _directory,
        }).Build();
        Assert.Equal(Core.Diagnostics.ProbeStatus.Skipped,
            (await new BackupReadinessProbe(configuration).CheckAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L)]
    public void RotationPreservesUnknownArchiveAndChecksum(long maxBytes)
    {
        Directory.CreateDirectory(_directory);
        Write("manual.dump", DateTime.UtcNow.AddYears(-1));
        Write("20261117T0000000000000-0123456789abcdef0123456789abcdef.dump", DateTime.UtcNow);
        var service = new BackupService(new BackupSettings { Directory = _directory, Daily = 1, Weekly = 0, Monthly = 0, MaxBytes = maxBytes });
        if (maxBytes == 1) Assert.Throws<IOException>(service.Rotate);
        else service.Rotate();
        Assert.Equal("синтетический архив", File.ReadAllText(Path.Combine(_directory, "manual.dump")));
        Assert.Equal("синтетическая сумма", File.ReadAllText(Path.Combine(_directory, "manual.dump.sha256")));
    }

    [Fact]
    public async Task ReadinessRejectsCopyOlderThanConfiguredIntervals()
    {
        Directory.CreateDirectory(_directory);
        var old = DateTimeOffset.UtcNow.AddHours(-3);
        File.WriteAllText(Path.Combine(_directory, "status.json"), JsonSerializer.Serialize(new BackupState(old, null, null, null, old)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = _directory,
            ["Backup:IntervalSeconds"] = "3600",
        }).Build();
        var result = await new BackupReadinessProbe(configuration).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Core.Diagnostics.ProbeStatus.Skipped, result.Status);
    }

    [Fact]
    public void RotationUnifiesCalendarBucketsAndPreservesVerifiedCopy()
    {
        const string old = "20200101T0000000000000-00000000000000000000000000000001.dump";
        const string middle = "20210101T0000000000000-00000000000000000000000000000002.dump";
        const string latest = "20220101T0000000000000-00000000000000000000000000000003.dump";
        Directory.CreateDirectory(_directory);
        var settings = new BackupSettings { Directory = _directory, Daily = 1, Weekly = 1, Monthly = 1 };
        Write(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(_directory, old + ".verified"), "проверено");
        Write(middle, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Write(latest, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = new BackupService(settings);
        service.Rotate();
        Assert.Equal([latest, old], service.List());
        Assert.False(File.Exists(Path.Combine(_directory, middle + ".sha256")));
        service.Rotate();
        Assert.Equal(2, service.List().Length);
    }

    [Fact]
    public async Task BackupFailureRecordsFailureAndDoesNotRotateLastSuccess()
    {
        Directory.CreateDirectory(_directory);
        Write("last.dump", DateTime.UtcNow);
        var success = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(Path.Combine(_directory, "status.json"), JsonSerializer.Serialize(new BackupState(success, null, null, "last.dump", null)));
        var service = new BackupService(new BackupSettings { Directory = _directory, PgDump = Path.Combine(_directory, "missing-executable") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["last.dump"], service.List());
        Assert.Equal(success, service.Status().LastSuccess);
        Assert.NotNull(service.Status().LastFailure);
        Assert.Equal("backup_failed", service.Status().FailureCategory);
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    public async Task CorruptArchiveAndPathTraversalAreRejectedBeforeRestore()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "corrupt.dump");
        File.WriteAllText(path, "синтетическое повреждение");
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData([1, 2, 3])));
        var service = new BackupService(new BackupSettings { Directory = _directory });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync("corrupt.dump", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.VerifyAsync("../secret.dump", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CapacityFailureKeepsVerifiedAndLatestArchives()
    {
        Directory.CreateDirectory(_directory);
        Write("verified.dump", DateTime.UtcNow.AddDays(-1));
        Write("latest.dump", DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_directory, "verified.dump.verified"), "проверено");
        File.WriteAllText(Path.Combine(_directory, "status.json"), JsonSerializer.Serialize(new BackupState(DateTimeOffset.UtcNow, null, null, "latest.dump", DateTimeOffset.UtcNow)));
        var service = new BackupService(new BackupSettings { Directory = _directory, MaxBytes = 1 });
        Assert.Throws<IOException>(service.Rotate);
        Assert.Equal(2, service.List().Length);
    }

    [Fact]
    public void InvalidScheduleAndParallelismAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new BackupService(new BackupSettings { Directory = _directory, IntervalSeconds = 0 }));
        Assert.Throws<ArgumentException>(() => new BackupService(new BackupSettings { Directory = _directory, RestoreJobs = 17 }));
        Assert.Throws<ArgumentException>(() => new BackupService(new BackupSettings { Directory = _directory, Daily = 0 }));
        var service = new BackupService(new BackupSettings { Directory = _directory });
        Assert.Null(service.Status().LastSuccess);
        Assert.Empty(service.List());
    }

    private void Write(string name, DateTime time)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "синтетический архив");
        File.SetLastWriteTimeUtc(path, time);
        File.WriteAllText(path + ".sha256", "синтетическая сумма");
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Каталог теста вышел за пределы TEMP.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        GC.SuppressFinalize(this);
    }
}