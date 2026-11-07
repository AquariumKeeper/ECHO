using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Infrastructure.Sessions;

/// <summary>
/// One running desktop window as registered for live-session control. Pipe and Token are the private endpoint and
/// its credential; the CLI uses them to connect but never prints them. The pack fields let a client check that the
/// pack it reads from disk is the one the window loaded.
/// </summary>
public sealed record SessionRegistration(
    string SessionId,
    int ProcessId,
    DateTimeOffset StartedAt,
    string PackId,
    string PackVersion,
    string PackDirectory,
    string PackManifestSha256,
    string Pipe,
    string Token);

/// <summary>A registry file as listed: its registration, or why it could not be read.</summary>
public sealed record SessionRegistryEntry(string SessionId, SessionRegistration? Registration, string? Problem);

/// <summary>
/// The per-user folder of running windows, one <c>&lt;session id&gt;.json</c> file each. A window publishes its own file
/// atomically and removes it on close. A crashed window's file stays until the next window starts, which removes the
/// files of processes that are no longer running; until then clients report it unavailable without contacting it.
/// The folder lives under the user's local app data, which only that user can read.
/// </summary>
public static class SessionRegistry
{
    public const string EnvironmentVariable = "CHARACTER_PLANNER_SESSION_DIR";
    public const string Extension = ".json";
    /// <summary>Listing reads at most this many registrations.</summary>
    public const int Limit = 128;
    private const int MaximumBytes = 64 * 1024;

    /// <summary>The folder <see cref="EnvironmentVariable"/> names, when it is an absolute path, otherwise the app data default.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)
            ? directory
            : Path.Combine(AppDataPaths.Folder, "sessions");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Ids are 32 lowercase hex digits, so one can never name a path outside the registry.</summary>
    public static bool IsId(string id) => id.Length == 32 && !id.AsSpan().ContainsAnyExcept("0123456789abcdef");

    public static async Task PublishAsync(string directory, SessionRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!IsId(registration.SessionId)) { throw new ArgumentException("Session ids are 32 lowercase hex digits", nameof(registration)); }
        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAsync(Path.Combine(directory, registration.SessionId + Extension), JsonSerializer.SerializeToUtf8Bytes(registration, Json),
            replace: false, TimeSpan.FromSeconds(5), exception => exception, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a window's own file; a missing file is already gone.</summary>
    public static void Remove(string directory, string sessionId)
    {
        if (!IsId(sessionId)) { throw new ArgumentException("Session ids are 32 lowercase hex digits", nameof(sessionId)); }
        try { File.Delete(Path.Combine(directory, sessionId + Extension)); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// Whether the registering process is still running: one with that id that started no later than it registered,
    /// so a reused process id doesn't keep a dead window listed. A process the user can't inspect counts as running.
    /// </summary>
    public static bool IsRunning(SessionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        try
        {
            using var process = Process.GetProcessById(registration.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= registration.StartedAt.UtcDateTime.AddSeconds(5);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    /// <summary>Removes the registrations of windows whose process has ended; unreadable files are left for a person.</summary>
    public static async Task PruneAsync(string directory, CancellationToken cancellationToken = default)
    {
        foreach (var entry in await ListAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Registration is { } registration && !IsRunning(registration))
            {
                try { Remove(directory, entry.SessionId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Every registration in id order, unreadable ones with why. A missing folder lists as empty and isn't created.</summary>
    public static async Task<ImmutableArray<SessionRegistryEntry>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) { return []; }
        var entries = ImmutableArray.CreateBuilder<SessionRegistryEntry>();
        var files = Directory.EnumerateFiles(directory, "*" + Extension)
            .Select(path => (Path: path, Id: Path.GetFileName(path)[..^Extension.Length]))
            .Where(file => IsId(file.Id)).OrderBy(file => file.Id, StringComparer.Ordinal).Take(Limit);
        foreach (var (path, id) in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaximumBytes) { entries.Add(new(id, null, "the registration is too large")); continue; }
                var registration = JsonSerializer.Deserialize<SessionRegistration>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), Json);
                entries.Add(registration is { SessionId: var stored } && stored == id && registration.Pipe is { Length: > 0 } && registration.Token is { Length: > 0 }
                    ? new(id, registration, null)
                    : new(id, null, "the registration is incomplete"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { entries.Add(new(id, null, "the registration can't be read: " + exception.Message)); }
            catch (JsonException) { entries.Add(new(id, null, "the registration is not valid JSON")); }
        }
        return entries.ToImmutable();
    }
}
