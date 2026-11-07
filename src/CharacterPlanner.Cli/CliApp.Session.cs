using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Infrastructure.Sessions;

namespace CharacterPlanner.Cli;

/// <summary>
/// Live control of running desktop windows (ADR 0007). The registry lists windows; each request goes to one window's
/// private, authenticated pipe. Edits name the revision they were based on, so a stale one is refused (exit 6) with
/// the current snapshot rather than overwriting what changed. Endpoints and tokens are never printed.
/// </summary>
internal static partial class CliApp
{
    public const int StaleRevision = 6;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static string SessionDirectory(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--session-dir", out var directory)) { return SessionRegistry.DefaultDirectory; }
        if (directory.Length == 0) { throw new CliInputException("session-dir-invalid", directory, "Session directory must not be empty"); }
        try { return Path.GetFullPath(directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new CliInputException("session-dir-invalid", directory, exception.Message); }
    }

    private static long Revision(Dictionary<string, string> options, string option)
    {
        var text = options[option];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : throw new CliInputException("session-request-invalid", option, $"{option} must be a revision number from 0");
    }

    private static async Task<int> ListSessionsAsync(Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var directory = SessionDirectory(options);
        var entries = await SessionRegistry.ListAsync(directory, cancellationToken).ConfigureAwait(false);
        var probes = await Task.WhenAll(entries.Select(entry => ProbeAsync(entry))).ConfigureAwait(false);
        var listed = new List<object>();
        foreach (var (entry, snapshot, problem) in probes)
        {
            listed.Add(new
            {
                sessionId = entry.SessionId,
                processId = entry.Registration?.ProcessId,
                startedAt = entry.Registration is { } started ? Time(started.StartedAt) : null,
                packId = entry.Registration?.PackId,
                packVersion = entry.Registration?.PackVersion,
                available = snapshot is not null,
                buildName = snapshot?.GetProperty("buildName").GetString(),
                revision = snapshot?.GetProperty("revision").GetInt64(),
                problem = snapshot is null ? problem : null
            });
        }
        output.WriteLine(JsonSerializer.Serialize(new { ok = true, result = new { directory, sessions = listed }, diagnostics = Array.Empty<object>() }));
        return Success;
    }

    /// <summary>A window's current snapshot, or why it can't be used. A window whose process has ended isn't contacted.</summary>
    private static async Task<(SessionRegistryEntry Entry, JsonElement? Snapshot, string? Problem)> ProbeAsync(SessionRegistryEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Registration is not { } registration) { return (entry, null, entry.Problem); }
        if (!SessionRegistry.IsRunning(registration)) { return (entry, null, "the window's process is no longer running"); }
        var reply = await TrySendAsync(registration, new JsonObject { ["command"] = SessionRequests.Inspect }, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        return reply is { } answer && answer.GetProperty("ok").GetBoolean()
            ? (entry, answer.GetProperty("snapshot"), null)
            : (entry, null, "the window is not responding; it may be busy or have closed without removing its registration");
    }

    private static async Task<JsonElement?> TrySendAsync(SessionRegistration registration, JsonObject request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var reply = await SessionPipe.SendAsync(registration, request, timeout, cancellationToken).ConfigureAwait(false);
            return reply.RootElement.Clone();
        }
        // A timeout means the window didn't answer; the user's own cancellation still cancels the command.
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidDataException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return null; }
    }

    /// <summary>The named window, or the only available one; anything else is exit 4.</summary>
    private static async Task<SessionRegistration> SelectSessionAsync(Dictionary<string, string> options, CancellationToken cancellationToken = default)
    {
        var directory = SessionDirectory(options);
        var entries = await SessionRegistry.ListAsync(directory, cancellationToken).ConfigureAwait(false);
        if (options.TryGetValue("--session", out var id))
        {
            var entry = entries.FirstOrDefault(candidate => candidate.SessionId == id)
                ?? throw new CliInputException("session-not-found", id, $"No window is registered as session {id}", IoFailure);
            return entry.Registration ?? throw new CliInputException("session-unavailable", id, $"Session {id} can't be used: {entry.Problem}", IoFailure);
        }
        var probes = await Task.WhenAll(entries.Select(entry => ProbeAsync(entry))).ConfigureAwait(false);
        // A window busy for longer than the probe (a long simulation, a loaded machine) is running but silent. Before
        // reporting none, give the silent windows of running processes one more probe; dead ones aren't contacted again.
        if (!probes.Any(probe => probe.Snapshot is not null))
        {
            probes = await Task.WhenAll(probes.Select(probe => probe.Entry.Registration is { } registration && SessionRegistry.IsRunning(registration)
                ? ProbeAsync(probe.Entry) : Task.FromResult(probe))).ConfigureAwait(false);
        }
        var available = probes.Where(probe => probe.Snapshot is not null).Select(probe => probe.Entry.Registration!).ToList();
        return available.Count switch
        {
            1 => available[0],
            0 => throw new CliInputException("session-none", directory, "No running window is available for live control", IoFailure),
            _ => throw new CliInputException("session-ambiguous", directory, $"{available.Count} windows are available; pass --session with one of their ids", IoFailure)
        };
    }

    private static async Task<(SessionRegistration Registration, JsonElement Reply)> RequestAsync(Dictionary<string, string> options, JsonObject request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var registration = await SelectSessionAsync(options, cancellationToken).ConfigureAwait(false);
        var reply = await TrySendAsync(registration, request, timeout, cancellationToken).ConfigureAwait(false)
            ?? throw new CliInputException("session-unavailable", registration.SessionId, "The window did not answer", IoFailure);
        return (registration, reply);
    }

    private static object Session(SessionRegistration registration) =>
        new { sessionId = registration.SessionId, processId = registration.ProcessId, packId = registration.PackId, packVersion = registration.PackVersion };

    /// <summary>Writes a window's reply. A refused request keeps its session code; a stale revision is exit 6 with the current snapshot.</summary>
    private static int SessionReply(TextWriter output, SessionRegistration registration, JsonElement reply, Func<JsonElement, object> result)
    {
        if (reply.GetProperty("ok").GetBoolean())
        {
            output.WriteLine(JsonSerializer.Serialize(new { ok = true, result = result(reply), diagnostics = Array.Empty<object>() }));
            return Success;
        }
        var error = reply.GetProperty("error");
        var code = error.GetProperty("code").GetString()!;
        var stale = code == SessionErrorCodes.StaleRevision;
        var diagnostics = new[] { new { severity = "error", code, path = registration.SessionId, message = error.GetProperty("message").GetString()! } };
        var errors = reply.TryGetProperty("operationErrors", out var listed) && listed.GetArrayLength() > 0 ? listed : default(JsonElement?);
        // Only a stale revision carries a result (the current snapshot); absent fields are left out rather than null.
        output.WriteLine(stale
            ? JsonSerializer.Serialize(new { ok = false, result = result(reply), diagnostics, operationErrors = errors }, OmitNulls)
            : JsonSerializer.Serialize(new { ok = false, diagnostics, operationErrors = errors }, OmitNulls));
        return stale ? StaleRevision : code == SessionPipe.Unauthorized ? IoFailure : InvalidInput;
    }

    private static object Snapshot(SessionRegistration registration, JsonElement reply)
    {
        var snapshot = reply.GetProperty("snapshot");
        return new
        {
            session = Session(registration),
            revision = snapshot.GetProperty("revision").GetInt64(),
            buildName = snapshot.GetProperty("buildName").GetString(),
            document = snapshot.GetProperty("document"),
            context = snapshot.GetProperty("context"),
            canUndo = snapshot.GetProperty("canUndo").GetBoolean(),
            canRedo = snapshot.GetProperty("canRedo").GetBoolean(),
            changes = reply.GetProperty("changes")
        };
    }

    private static async Task<int> InspectSessionAsync(Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["command"] = SessionRequests.Inspect };
        var timeout = RequestTimeout;
        if (options.TryGetValue("--wait", out var wait))
        {
            if (!int.TryParse(wait, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds > SessionRequests.MaximumWait.TotalMilliseconds)
            { throw new CliInputException("session-request-invalid", "--wait", $"--wait must be milliseconds from 0 to {SessionRequests.MaximumWait.TotalMilliseconds}"); }
            request["waitMilliseconds"] = milliseconds;
            timeout += TimeSpan.FromMilliseconds(milliseconds);
        }
        if (options.ContainsKey("--since-revision"))
        {
            if (request["waitMilliseconds"] is not { } waiting || waiting.GetValue<int>() == 0)
            { throw new CliInputException("session-request-invalid", "--since-revision", "--since-revision needs a --wait above 0"); }
            request["sinceRevision"] = Revision(options, "--since-revision");
        }
        var (registration, reply) = await RequestAsync(options, request, timeout, cancellationToken).ConfigureAwait(false);
        return SessionReply(output, registration, reply, answer => Snapshot(registration, answer));
    }

    private static async Task<int> SessionHistoryAsync(Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var (registration, reply) = await RequestAsync(options, new JsonObject { ["command"] = SessionRequests.Inspect }, RequestTimeout, cancellationToken).ConfigureAwait(false);
        return SessionReply(output, registration, reply, answer => new
        {
            session = Session(registration),
            revision = answer.GetProperty("snapshot").GetProperty("revision").GetInt64(),
            history = answer.GetProperty("snapshot").GetProperty("history")
        });
    }

    /// <summary>The window's build and assumptions resolved here, as build resolve reports them, on the pack the window loaded.</summary>
    private static async Task<int> ResolveSessionAsync(Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var (registration, reply) = await RequestAsync(options, new JsonObject { ["command"] = SessionRequests.Inspect }, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.GetProperty("ok").GetBoolean()) { return SessionReply(output, registration, reply, answer => Snapshot(registration, answer)); }
        // The manifest pins every pack file, so a changed pack is caught before paying for (or failing) a compile.
        var directory = PackDirectory(registration.PackDirectory);
        if (PackFiles.Sha256(await File.ReadAllBytesAsync(Path.Combine(directory, PackFiles.Manifest), cancellationToken).ConfigureAwait(false)) != registration.PackManifestSha256)
        { throw new CliInputException("session-pack-changed", registration.PackDirectory, "The pack on disk is not the one the window loaded; reopen the window", IoFailure); }
        var loaded = await PackFileLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        if (loaded.Validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(loaded.Validation.Diagnostics)); }
        var snapshot = reply.GetProperty("snapshot");
        var build = BuildDocument.Decode(Encoding.UTF8.GetBytes(snapshot.GetProperty("document").GetRawText()));
        var assumptions = snapshot.GetProperty("context").EnumerateObject().ToImmutableDictionary(pair => pair.Name,
            pair => pair.Value.ValueKind == JsonValueKind.Number ? pair.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture) : pair.Value.GetString()!, StringComparer.Ordinal);
        var resolved = BuildResolver.Resolve(compiled, build, assumptions);
        var result = Resolution(compiled.Pack, resolved);
        result["session"] = Session(registration);
        result["revision"] = snapshot.GetProperty("revision").GetInt64();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result,
            diagnostics = resolved.Diagnostics.Select(diagnostic => new { severity = diagnostic.Severity, code = diagnostic.Code, path = diagnostic.Subject, message = diagnostic.Message })
        }));
        return Success;
    }

    private static async Task<int> ChangeSessionAsync(string command, Dictionary<string, string> options, Stream input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["command"] = command, ["expectedRevision"] = Revision(options, "--expected-revision") };
        if (command == SessionRequests.Apply)
        {
            var path = options["--operations"];
            var bytes = WithoutBom(path == "-" ? await ReadBoundedAsync(input, path, cancellationToken).ConfigureAwait(false) : await ReadInputAsync(path, cancellationToken).ConfigureAwait(false));
            try { request["operations"] = JsonNode.Parse(bytes.Span, documentOptions: new() { MaxDepth = 64 }); }
            catch (JsonException exception) { throw new CliInputException(SessionRequests.OperationsInvalid, path, "Operations are not valid JSON: " + exception.Message); }
        }
        var registration = await SelectSessionAsync(options, cancellationToken).ConfigureAwait(false);
        // Once an edit is sent the window may apply it, so cancellation stops only before sending; the reply (bounded by
        // the request timeout) then reports what actually happened rather than a cancellation that hid a change.
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await TrySendAsync(registration, request, RequestTimeout, CancellationToken.None).ConfigureAwait(false)
            ?? throw new CliInputException("session-unavailable", registration.SessionId, "The window did not answer; the edit may or may not have been applied", IoFailure);
        return SessionReply(output, registration, reply, answer => Snapshot(registration, answer));
    }
}
