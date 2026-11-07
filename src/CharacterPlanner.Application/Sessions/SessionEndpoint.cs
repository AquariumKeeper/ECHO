using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Application.Sessions;

/// <summary>Live-session request kinds and their stable error codes. Messages are for people; codes may not change.</summary>
public static class SessionRequests
{
    public const string Inspect = "inspect";
    public const string Apply = "apply";
    public const string Undo = "undo";
    public const string Redo = "redo";

    public const string RequestInvalid = "session-request-invalid";
    public const string OperationsInvalid = "operations-invalid";

    /// <summary>The longest an inspect may wait for a newer revision.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Answers live-session requests for one running session, without any transport: a host passes each request's JSON
/// and returns the reply's. Edits go through <see cref="PlannerSession"/> under its revision guard, as
/// <see cref="EditOrigin.Session"/> history entries, so a request based on an old revision is refused rather than
/// overwriting what the desktop did since. Safe to call concurrently; the session serializes changes.
/// </summary>
/// <remarks>
/// A request is <c>{"command": ..., "expectedRevision": n, "operations": [...], "sinceRevision": n, "waitMilliseconds": n}</c>
/// with only the command's fields. A reply is <c>{"ok", "snapshot", "changes", "operationErrors", "error"}</c>; the
/// snapshot carries the revision, pack identity, build document, typed context, undo/redo state and history.
/// </remarks>
public sealed class SessionEndpoint(PlannerSession session)
{
    private readonly PlannerSession session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<byte[]> HandleAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        JsonObject reply;
        try { reply = await HandleAsync(Parse(request), cancellationToken).ConfigureAwait(false); }
        catch (SessionRequestException exception) { reply = Refused(session.Snapshot, new(RequestInvalid, exception.Message)); }
        return JsonSerializer.SerializeToUtf8Bytes(reply);
    }

    private const string RequestInvalid = SessionRequests.RequestInvalid;

    private sealed class SessionRequestException(string message) : Exception(message);

    private sealed record Request(string Command, long? ExpectedRevision, JsonElement? Operations, long? SinceRevision, int WaitMilliseconds);

    private static Request Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new SessionRequestException("A request must be a JSON object"); }
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value.Clone())) { throw new SessionRequestException($"Request repeats '{property.Name}'"); }
            }
            var command = fields.TryGetValue("command", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : throw new SessionRequestException("A request needs a 'command'");
            string[] allowed = command switch
            {
                SessionRequests.Inspect => ["command", "sinceRevision", "waitMilliseconds"],
                SessionRequests.Apply => ["command", "expectedRevision", "operations"],
                SessionRequests.Undo or SessionRequests.Redo => ["command", "expectedRevision"],
                _ => throw new SessionRequestException($"Unknown command '{command}'")
            };
            if (fields.Keys.FirstOrDefault(field => !allowed.Contains(field)) is { } extra) { throw new SessionRequestException($"'{command}' takes no '{extra}'"); }
            long? Revision(string field) => !fields.TryGetValue(field, out var value) ? null
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number
                : throw new SessionRequestException($"'{field}' must be a revision number from 0");
            var expected = Revision("expectedRevision");
            if (command != SessionRequests.Inspect && expected is null) { throw new SessionRequestException($"'{command}' needs 'expectedRevision'"); }
            JsonElement? operations = fields.TryGetValue("operations", out var batch) ? batch : null;
            if (command == SessionRequests.Apply && operations is null) { throw new SessionRequestException("'apply' needs 'operations'"); }
            var wait = 0;
            if (fields.TryGetValue("waitMilliseconds", out var milliseconds))
            {
                wait = milliseconds.ValueKind == JsonValueKind.Number && milliseconds.TryGetInt32(out var value) && value >= 0 && value <= SessionRequests.MaximumWait.TotalMilliseconds
                    ? value : throw new SessionRequestException($"'waitMilliseconds' must be from 0 to {SessionRequests.MaximumWait.TotalMilliseconds}");
            }
            var since = Revision("sinceRevision");
            if (since is not null && wait == 0) { throw new SessionRequestException("'sinceRevision' needs 'waitMilliseconds'"); }
            return new(command, expected, operations, since, wait);
        }
        catch (JsonException exception) { throw new SessionRequestException("A request must be valid JSON: " + exception.Message); }
        catch (InvalidOperationException) { throw new SessionRequestException("A request contains text that is not valid Unicode"); }
    }

    private async Task<JsonObject> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case SessionRequests.Inspect:
                var snapshot = request.WaitMilliseconds > 0
                    ? await WaitAsync(request.SinceRevision ?? session.Snapshot.Revision, TimeSpan.FromMilliseconds(request.WaitMilliseconds), cancellationToken).ConfigureAwait(false)
                    : session.Snapshot;
                return Reply(new(snapshot, [], [], null));
            case SessionRequests.Apply:
                var parsed = OperationJson.Read(JsonSerializer.SerializeToUtf8Bytes(request.Operations!.Value));
                if (!parsed.Succeeded) { return Reply(new(session.Snapshot, [], parsed.Errors, new(SessionRequests.OperationsInvalid, "the operations could not be read; nothing was applied"))); }
                return Reply(session.Apply(parsed.Operations, request.ExpectedRevision!.Value, EditOrigin.Session));
            case SessionRequests.Undo: return Reply(session.Undo(request.ExpectedRevision!.Value));
            default: return Reply(session.Redo(request.ExpectedRevision!.Value));
        }
    }

    /// <summary>The first snapshot newer than <paramref name="since"/>, or the current one when the wait ends first.</summary>
    private async Task<SessionSnapshot> WaitAsync(long since, TimeSpan wait, CancellationToken cancellationToken)
    {
        var changed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionSnapshot snapshot) { if (snapshot.Revision > since) { changed.TrySetResult(snapshot); } }
        session.Changed += OnChanged;
        try
        {
            if (session.Snapshot is { } now && now.Revision > since) { return now; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            try { return await changed.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return session.Snapshot; }
        }
        finally { session.Changed -= OnChanged; }
    }

    private static JsonObject Refused(SessionSnapshot snapshot, SessionError error) => Reply(new(snapshot, [], [], error));

    private static JsonObject Reply(SessionResult result) => new()
    {
        ["ok"] = result.Succeeded,
        ["snapshot"] = Snapshot(result.Snapshot),
        ["changes"] = new JsonArray([.. result.Changes.Select(change => (JsonNode)new JsonObject
        {
            ["operation"] = change.Operation,
            ["kind"] = change.Kind,
            ["set"] = new JsonArray([.. change.Set.Select(path => (JsonNode)JsonValue.Create(path))]),
            ["cleared"] = new JsonArray([.. change.Cleared.Select(path => (JsonNode)JsonValue.Create(path))])
        })]),
        ["operationErrors"] = new JsonArray([.. result.OperationErrors.Select(error => (JsonNode)new JsonObject
        {
            ["operation"] = error.Operation,
            ["kind"] = error.Kind,
            ["code"] = error.Code,
            ["message"] = error.Message
        })]),
        ["error"] = result.Error is { } refused ? new JsonObject { ["code"] = refused.Code, ["message"] = refused.Message } : null
    };

    private static JsonObject Snapshot(SessionSnapshot snapshot)
    {
        var pack = snapshot.Pack.Pack;
        var numeric = pack.NumericContextInputs.Select(input => input.ID).ToHashSet(StringComparer.Ordinal);
        var context = new JsonObject();
        foreach (var (key, value) in snapshot.State.Assumptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        { context[key] = numeric.Contains(key) ? JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)) : JsonValue.Create(value); }
        return new JsonObject
        {
            ["revision"] = snapshot.Revision,
            ["packId"] = pack.ID,
            ["packVersion"] = pack.Version,
            ["buildName"] = snapshot.State.Build.Meta.Name,
            ["document"] = JsonNode.Parse(BuildDocument.Encode(snapshot.State.Build)),
            ["context"] = context,
            ["canUndo"] = snapshot.CanUndo,
            ["canRedo"] = snapshot.CanRedo,
            ["history"] = new JsonArray([.. snapshot.History.Select(entry => (JsonNode)new JsonObject
            {
                ["revision"] = entry.Revision,
                ["origin"] = JsonNamingPolicy.CamelCase.ConvertName(entry.Origin.ToString()),
                ["label"] = entry.Label,
                ["current"] = entry.Current
            })])
        };
    }
}
