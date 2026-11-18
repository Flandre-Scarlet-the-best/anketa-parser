using System.Text.Json;

using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Sources;

/// <summary>Общий сценарий опроса и чтения состояния для хоста и CLI.</summary>
public sealed class DvachIngestion
{
    private readonly DvachSettings _settings;
    private readonly DatabaseSettings _database;
    private readonly DvachClient _client;
    private readonly TimeProvider _clock;

    internal DvachIngestion(IOptions<DvachSettings> settings, IOptions<DatabaseSettings> database, DvachClient client, TimeProvider clock)
    {
        _settings = settings.Value;
        _database = database.Value;
        _client = client;
        _clock = clock;
    }

    public bool IsEnabled => DvachThread.Parse(_settings.ThreadUrl) is not null;
    public bool IsConfigured => IsEnabled && !string.IsNullOrWhiteSpace(_database.ConnectionString);
    public TimeSpan PollInterval => TimeSpan.FromSeconds(_settings.PollIntervalSeconds);

    public async Task<DvachScanReport> ScanAsync(string? fixturePath, CancellationToken ct)
    {
        var thread = DvachThread.Parse(_settings.ThreadUrl);
        if (thread is null || !IsConfigured)
            return Report("not_configured", "configuration");

        var key = fixturePath is null ? thread.SourceKey : thread.FixtureKey;
        try
        {
            await using var db = CreateContext();
            var store = new DvachScanStore(db);
            if (!await store.TryLockAsync(key, ct).ConfigureAwait(false))
                return Report("busy", "store");

            var state = await store.LoadAsync(key, thread.Url, ct).ConfigureAwait(false);
            if (state.RetryAt > _clock.GetUtcNow())
                return DvachScanStore.Report(state) with { Status = "backoff" };

            try
            {
                if (state.Pending is null)
                {
                    await StartAsync(store, state, ct).ConfigureAwait(false);
                    var snapshot = await _client.FetchAsync(thread, _settings, fixturePath, ct).ConfigureAwait(false);
                    if (snapshot.Posts.Count == 0)
                    {
                        return await store.RecoverFailureAsync(state, _clock.GetUtcNow(), _settings.PollIntervalSeconds,
                            "empty_snapshot", "parse", null, null, ct).ConfigureAwait(false);
                    }

                    List<PreparedPost> posts = new(snapshot.Posts.Count);
                    foreach (var post in snapshot.Posts)
                    {
                        ct.ThrowIfCancellationRequested();
                        posts.Add(DvachFilter.Prepare(thread, post, key));
                    }
                    state.Pending = JsonSerializer.Serialize(posts);
                    state.Metrics = JsonSerializer.Serialize(new DvachScanMetrics(snapshot.Posts.Count, snapshot.Closed));
                    state.Phase = "apply";
                    // Журнал сохраняется до первого импорта. После сбоя он воспроизводится без HTTP.
                    await store.SaveAsync(ct).ConfigureAwait(false);
                }

                var pending = JsonSerializer.Deserialize<PreparedPost[]>(state.Pending)!;
                for (var index = state.Applied; index < pending.Length; index++)
                    await store.ApplyAsync(state, pending[index], ct).ConfigureAwait(false);

                state.Pending = null;
                state.LastSuccessAt = state.FinishedAt = _clock.GetUtcNow();
                state.Status = "complete";
                state.Phase = "complete";
                state.Failure = null;
                state.HttpStatus = null;
                state.ConsecutiveFailures = 0;
                state.RetryAt = null;
                await store.SaveAsync(ct).ConfigureAwait(false);
                return DvachScanStore.Report(state);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                {
                    using var recordDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await store.MarkInterruptedAsync(state.SourceId, _clock.GetUtcNow(), recordDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is OperationCanceledException || IsDatabaseError(error, CancellationToken.None))
                    {
                        // Журнал остаётся в PostgreSQL. Следующий процесс определит точку продолжения.
                    }
                }
                throw;
            }
            catch (SourceFailure error)
            {
                return await store.RecoverFailureAsync(state, _clock.GetUtcNow(), _settings.PollIntervalSeconds,
                    error.Category, error.Phase, error.HttpStatus, error.RetryAt, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (IsDatabaseError(error, ct) &&
                db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            {
                return await store.RecoverFailureAsync(state, _clock.GetUtcNow(), _settings.PollIntervalSeconds,
                    DatabaseFailure(error), state.Phase, null, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsDatabaseError(error, ct))
        {
            return Report("failed", "store", DatabaseFailure(error));
        }
    }

    public Task<DvachScanReport> StatusAsync(bool fixture, CancellationToken ct) =>
        ReadAsync(fixture, (store, key) => store.StatusAsync(key, ct), ct);

    public Task<DvachPostReport> PostAsync(bool fixture, long postNumber, CancellationToken ct) =>
        ReadAsync(fixture, (store, key) => store.PostAsync(key, postNumber, ct), ct);

    public Task<IReadOnlyList<DvachQueueCount>> QueueAsync(bool fixture, CancellationToken ct) =>
        ReadAsync(fixture, (store, key) => store.QueueAsync(key, ct), ct);

    private async Task<TResult> ReadAsync<TResult>(bool fixture, Func<DvachScanStore, string, Task<TResult>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var thread = DvachThread.Parse(_settings.ThreadUrl);
        if (thread is null || !IsConfigured)
            throw new InvalidOperationException("Для чтения состояния требуется настройка треда и PostgreSQL.");

        try
        {
            await using var db = CreateContext();
            return await read(new DvachScanStore(db), fixture ? thread.FixtureKey : thread.SourceKey).ConfigureAwait(false);
        }
        catch (Exception error) when (IsDatabaseError(error, ct))
        {
            throw new DependencyUnavailableException(DatabaseFailure(error), error);
        }
    }

    private async Task StartAsync(DvachScanStore store, SourceScan state, CancellationToken ct)
    {
        state.StartedAt = _clock.GetUtcNow();
        state.FinishedAt = null;
        state.Status = "running";
        state.Phase = "fetch";
        state.Failure = null;
        state.HttpStatus = null;
        state.RetryAt = null;
        state.Applied = 0;
        state.Metrics = "{}";
        await store.SaveAsync(ct).ConfigureAwait(false);
    }

    private ProductDbContext CreateContext()
    {
        var connection = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Pooling = false,
            Multiplexing = false,
        };
        return ProductDbContext.Create(connection.ConnectionString);
    }

    private static bool IsDatabaseError(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested && error is NpgsqlException or DbUpdateException or TimeoutException or
            InvalidOperationException { InnerException: NpgsqlException or TimeoutException };

    private static string DatabaseFailure(Exception error) =>
        (error is DbUpdateException or InvalidOperationException ? error.InnerException : error)
            is PostgresException { SqlState: "42P01" or "42703" }
            ? "schema_unavailable"
            : "database_unavailable";

    private static DvachScanReport Report(string status, string phase, string? failure = null) =>
        new(status, phase, failure, null, null, null, null, null, 0, false, 0, null);
}