using AnketaParser.Cli;

return await CliApplication
    .RunAsync(args, Console.Out, Console.Error, CancellationToken.None)
    .ConfigureAwait(false);