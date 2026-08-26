using ValidatedWorld.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await CliRunner.RunAsync(
    args,
    Console.In,
    Console.Out,
    Console.Error,
    cancellation.Token);
