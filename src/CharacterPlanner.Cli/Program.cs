using CharacterPlanner.Cli;

// A thin entry point: CliApp holds the commands, so tests run them in process.
// The first Ctrl+C cancels long work (a simulation, a session wait) so the command still answers in JSON; a second
// one ends the process as usual, for work that can't be interrupted (such as a console read already under way).
using var cancellation = new CancellationTokenSource();
var presses = 0;
void OnCancel(object? sender, ConsoleCancelEventArgs press)
{
    if (Interlocked.Increment(ref presses) > 1) { return; }
    press.Cancel = true;
    cancellation.Cancel();
}
Console.CancelKeyPress += OnCancel;
try { return await CliApp.RunAsync(args, Console.Out, Console.Error, Console.OpenStandardInput(), cancellation.Token); }
// Unsubscribed before the source is disposed, so a late Ctrl+C can't reach a disposed source.
finally { Console.CancelKeyPress -= OnCancel; }
