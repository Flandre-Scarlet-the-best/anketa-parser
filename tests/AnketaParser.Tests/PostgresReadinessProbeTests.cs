using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AnketaParser.Tests;

public sealed class PostgresReadinessProbeTests
{
    [Fact]
    public async Task ServerErrorLogsClassificationAndEventIdWithoutExceptionOrSecrets()
    {
        const string secret = "postgres-exception-secret-sentinel";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = SendErrorAsync(listener, secret, timeout.Token);

        var logger = new CapturingLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = $"Host=127.0.0.1;Port={port};Database=synthetic;Username=test;Password={secret};SSL Mode=Disable;GSS Encryption Mode=Disable",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnketaParserInfrastructure(configuration, "Test", []);
        services.AddSingleton<ILogger<PostgresReadinessProbe>>(logger);
        using var provider = services.BuildServiceProvider();

        var probe = Assert.Single(provider.GetServices<IReadinessProbe>());
        var result = await probe.CheckAsync(timeout.Token);
        await server;

        Assert.Equal(ProbeStatus.Unhealthy, result.Status);
        Assert.Contains("28P01", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Detail, StringComparison.Ordinal);
        Assert.Equal(2001, logger.EventId.Id);
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Null(logger.Exception);
        Assert.Equal($"127.0.0.1:{port}/synthetic", logger.Fields["Target"]);
        Assert.Equal("ошибка сервера 28P01", logger.Fields["FailureCategory"]);
        Assert.DoesNotContain(secret, logger.Message + string.Join(' ', logger.Fields.Values), StringComparison.Ordinal);
    }

    private static async Task SendErrorAsync(TcpListener listener, string message, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(header) - 4];
        await stream.ReadExactlyAsync(startup, cancellationToken);

        // Синтетический ErrorResponse: серверное сообщение намеренно содержит секрет-сентинел.
        var body = Encoding.UTF8.GetBytes($"SFATAL\0VFATAL\0C28P01\0M{message}\0\0");
        var packet = new byte[5 + body.Length];
        packet[0] = (byte)'E';
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1), 4 + body.Length);
        body.CopyTo(packet, 5);
        await stream.WriteAsync(packet, cancellationToken);
    }

    private sealed class CapturingLogger : ILogger<PostgresReadinessProbe>
    {
        public EventId EventId { get; private set; }
        public LogLevel Level { get; private set; }
        public Exception? Exception { get; private set; }
        public string Message { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, object?> Fields { get; private set; } = new Dictionary<string, object?>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            EventId = eventId;
            Level = logLevel;
            Exception = exception;
            Message = formatter(state, exception);
            Fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary();
        }
    }
}