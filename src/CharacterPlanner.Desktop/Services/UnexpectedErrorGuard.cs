using System.Globalization;

namespace CharacterPlanner.Desktop.Services;

/// <summary>
/// The app's last resort (F94): an exception that escapes to the UI dispatcher is recorded and reported, and the app stays
/// open, so a failure never takes unsaved work with it. Calculations already run in jobs that turn their failures into an
/// error state; this catches what's left. A failure that keeps coming back (more than <see cref="BurstLimit"/> within
/// <see cref="BurstWindow"/>, one on every frame, say) isn't held any longer, and the app closes rather than spin. Failures
/// on other threads, which can't be held, are only recorded.
/// </summary>
public sealed class UnexpectedErrorGuard(string logPath, Action<string> report, TimeProvider? clock = null)
{
    /// <summary>How many failures in <see cref="BurstWindow"/> are still held.</summary>
    public const int BurstLimit = 10;

    /// <summary>Past this size the log moves to <c>.1</c>, replacing the one there, and a new one starts.</summary>
    public const long MaximumLogBytes = 1024 * 1024;

    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> recent = new();
    // The UI thread, the finalizer's unobserved tasks and other threads all write here.
    private readonly Lock writing = new();

    public string LogPath => logPath;

    /// <summary>Records a failure and reports it, returning whether the app should carry on.</summary>
    public bool Handle(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var now = time.GetUtcNow();
        while (recent.Count > 0 && now - recent.Peek() > BurstWindow) { recent.Dequeue(); }
        recent.Enqueue(now);
        var logged = Log(exception.ToString(), now);
        if (recent.Count > BurstLimit)
        {
            Log($"More than {BurstLimit} failures within {BurstWindow.TotalSeconds:0} s: letting this one close the app.", now);
            return false;
        }
        var details = logged ? $" Details are in {logPath}." : "";
        var message = $"Something went wrong and was stopped: {Sentence(exception.Message)} The planner stayed open. Save your build, and restart the planner if anything looks wrong.{details}";
        try { report(message); }
#pragma warning disable CA1031 // The report runs inside the last-resort handler; its own failure must not close the app.
        catch (Exception failed)
#pragma warning restore CA1031
        { Log("The report itself failed: " + failed, now); }
        return true;
    }

    /// <summary>Records a failure nobody can hold, such as a background task's or another thread's, without reporting it.</summary>
    public void Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log(exception.ToString(), time.GetUtcNow());
    }

    // One line for the banner, ending as a sentence.
    private static string Sentence(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length == 0) { return "no description was given."; }
        return line[^1] is '.' or '!' or '?' ? line : line + ".";
    }

    // Appends an entry, best effort: an unwritable log never stops the report.
    private bool Log(string text, DateTimeOffset at)
    {
        lock (writing)
        {
            try
            {
                if (Path.GetDirectoryName(logPath) is { Length: > 0 } folder) { Directory.CreateDirectory(folder); }
                var file = new FileInfo(logPath);
                if (file.Exists && file.Length > MaximumLogBytes) { File.Move(logPath, logPath + ".1", overwrite: true); }
                File.AppendAllText(logPath, $"{at.ToString("O", CultureInfo.InvariantCulture)} {text}{Environment.NewLine}{Environment.NewLine}");
                return true;
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return false;
            }
        }
    }
}
