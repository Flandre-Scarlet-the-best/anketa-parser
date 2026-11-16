using System.Runtime.InteropServices;

using AnketaParser.Cli;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; shutdown.Cancel(); });
return await CliApplication
    .RunAsync(args, Console.Out, Console.Error, shutdown.Token)
    .ConfigureAwait(false);