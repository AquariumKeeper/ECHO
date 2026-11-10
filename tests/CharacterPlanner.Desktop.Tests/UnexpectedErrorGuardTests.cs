using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

// F94: a failure that escapes to the UI thread is recorded and reported, and the app stays open.
public sealed class UnexpectedErrorGuardTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "planner-guard-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(folder, "errors.log");

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void AFailureIsLoggedAndReportedAndTheAppCarriesOn()
    {
        var reports = new List<string>();
        var guard = new UnexpectedErrorGuard(LogPath, reports.Add, new Clock());
        Assert.True(guard.Handle(new InvalidOperationException("The stat page broke.")));
        var report = Assert.Single(reports);
        Assert.Contains("The stat page broke.", report, StringComparison.Ordinal);
        Assert.Contains("The planner stayed open", report, StringComparison.Ordinal);
        Assert.Contains(LogPath, report, StringComparison.Ordinal);
        var log = File.ReadAllText(LogPath);
        Assert.Contains("2026-11-08T12:00:00", log, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: The stat page broke.", log, StringComparison.Ordinal);
        // A second one is added, not written over.
        guard.Handle(new ArgumentException("Again."));
        Assert.Contains("The stat page broke.", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.Contains("Again.", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureThatKeepsComingBackIsLetGo()
    {
        var clock = new Clock();
        var reports = new List<string>();
        var guard = new UnexpectedErrorGuard(LogPath, reports.Add, clock);
        // Ten within the window are held; the eleventh isn't, so the app closes rather than spin.
        for (var index = 0; index < UnexpectedErrorGuard.BurstLimit; index++) { Assert.True(guard.Handle(new InvalidOperationException("frame"))); }
        Assert.False(guard.Handle(new InvalidOperationException("frame")));
        Assert.Equal(UnexpectedErrorGuard.BurstLimit, reports.Count);
        // The log says why the app closed.
        Assert.Contains("letting this one close the app", File.ReadAllText(LogPath), StringComparison.Ordinal);
        // Spread out, they're all held.
        var spread = new UnexpectedErrorGuard(LogPath, _ => { }, clock);
        for (var index = 0; index < 3 * UnexpectedErrorGuard.BurstLimit; index++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(spread.Handle(new InvalidOperationException("now and then")));
        }
    }

    [Fact]
    public void ALogPastItsLimitMovesAsideAndAnUnwritableOneStillReports()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(LogPath, new string('x', (int)UnexpectedErrorGuard.MaximumLogBytes + 1));
        new UnexpectedErrorGuard(LogPath, _ => { }).Record(new InvalidOperationException("fresh"));
        var log = File.ReadAllText(LogPath);
        Assert.DoesNotContain("xxx", log, StringComparison.Ordinal);
        Assert.Contains("fresh", log, StringComparison.Ordinal);
        // The earlier entries are kept beside it, so the first failure of a cascade survives.
        Assert.StartsWith("xxx", File.ReadAllText(LogPath + ".1"), StringComparison.Ordinal);

        // The log's path is a folder, so writing fails: the report still comes, without pointing at a log.
        var reports = new List<string>();
        var blocked = Path.Combine(folder, "blocked");
        Directory.CreateDirectory(blocked);
        Assert.True(new UnexpectedErrorGuard(blocked, reports.Add).Handle(new InvalidOperationException("no log")));
        Assert.DoesNotContain("Details are in", Assert.Single(reports), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AnExceptionOnTheUiThreadReachesTheGuardAndIsHandled()
    {
        var reports = new List<string>();
        var guard = new UnexpectedErrorGuard(LogPath, reports.Add, new Clock());
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args) => args.Handled = guard.Handle(args.Exception);
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            var after = false;
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("A command broke."));
            Dispatcher.UIThread.Post(() => after = true);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("A command broke.", Assert.Single(reports), StringComparison.Ordinal);
            // The dispatcher carries on with the next job.
            Assert.True(after);
        }
        finally { Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [Fact]
    public void AFailingReportIsLoggedAndStillHeld()
    {
        var guard = new UnexpectedErrorGuard(LogPath, _ => throw new InvalidOperationException("The banner broke too."), new Clock());
        Assert.True(guard.Handle(new InvalidOperationException("First.")));
        Assert.Contains("The report itself failed: System.InvalidOperationException: The banner broke too.", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sequence contains no elements", "stopped: Sequence contains no elements. The planner")]
    [InlineData("Done!", "stopped: Done! The planner")]
    [InlineData("Two\r\nlines.", "stopped: Two lines. The planner")]
    [InlineData("  ", "stopped: no description was given. The planner")]
    public void TheReportReadsAsSentences(string message, string expected)
    {
        var reports = new List<string>();
        new UnexpectedErrorGuard(LogPath, reports.Add, new Clock()).Handle(new InvalidOperationException(message));
        Assert.Contains(expected, Assert.Single(reports), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AReportDuringLoadingIsShownByTheEditorOnceItOpens()
    {
        using var model = new MainViewModel();
        model.ReportUnexpected("Something went wrong.");
        Assert.Equal("Something went wrong.", model.Status);
        Assert.Null(model.Error);
        await model.LoadAsync(Path.Combine(AppContext.BaseDirectory, "packs/division-2"), new FileBuildLibrary(Path.Combine(folder, "library")), AvaloniaDispatcher.Instance);
        Assert.Contains("Something went wrong.", model.Editor!.Message, StringComparison.Ordinal);
        // Once it's open, a report goes straight to the editor.
        model.ReportUnexpected("Again.");
        Assert.Equal("Again.", model.Editor.Message);
    }
}
