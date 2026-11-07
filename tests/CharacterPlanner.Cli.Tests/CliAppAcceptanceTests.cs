using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CharacterPlanner.Cli.Tests;

/// <summary>
/// C10 acceptance: one scenario through every command describe advertises, checking that each answers with the same
/// JSON envelope and that its exit code agrees with it. A command added to describe without a scenario fails here.
/// </summary>
public sealed partial class CliAppTests
{
    private sealed class Acceptance
    {
        public HashSet<string> Exercised { get; } = new(StringComparer.Ordinal);

        public async Task<Run> RunAsync(params string[] args)
        {
            Exercised.Add(Key(args));
            var run = await CliAppTests.RunAsync(args);
            CheckEnvelope(run);
            return run;
        }

        public async Task<JsonElement> OkAsync(params string[] args)
        {
            var run = await RunAsync(args);
            Assert.True(run.ExitCode == CliApp.Success, $"{string.Join(' ', args)} exited {run.ExitCode}: {run.Output}");
            return run.Json.GetProperty("result");
        }
    }

    /// <summary>A grammar's command: its first word, or its first two for grouped commands.</summary>
    private static string Key(IReadOnlyList<string> words) =>
        words.Count >= 2 && words[0] is "pack" or "share" or "catalog" or "build" or "library" or "session" ? words[0] + " " + words[1] : words[0];

    private static void CheckEnvelope(Run run)
    {
        var json = run.Json;
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        var ok = json.GetProperty("ok").GetBoolean();
        Assert.Equal(ok, run.ExitCode == CliApp.Success);
        foreach (var diagnostic in json.GetProperty("diagnostics").EnumerateArray())
        {
            Assert.Equal(new[] { "severity", "code", "path", "message" }, diagnostic.EnumerateObject().Select(property => property.Name));
            Assert.All(diagnostic.EnumerateObject(), property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));
        }
        if (ok) { Assert.True(json.TryGetProperty("result", out _), "A success carries a result"); }
        else { Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetProperty("severity").GetString() == "error"); }
    }

    [Fact]
    public async Task EveryAdvertisedCommandAnswersWithTheSameEnvelope()
    {
        var accept = new Acceptance();
        var described = await accept.OkAsync("describe");
        await accept.OkAsync("version");

        // Packs, catalog and share conversion.
        var mini = WriteMini(pack: WithContext);
        await accept.OkAsync("pack", "validate", mini);
        await accept.OkAsync("pack", "convert-v1", DivisionV1, Path.Combine(root, "converted"));
        Assert.Equal("mask_a", (await accept.OkAsync("catalog", "list", mini, "items")).GetProperty("entries")[0].GetProperty("id").GetString());
        await accept.OkAsync("catalog", "show", mini, "items", "mask_a");

        // Builds: create, edit, resolve, share, compare.
        var created = await accept.OkAsync("build", "new", mini);
        var build = Path.Combine(root, "accept.json");
        File.WriteAllText(build, created.GetRawText());
        var operations = WriteOperations("""[{"kind":"equipment","slot":"mask","id":"mask_a"},{"kind":"context","id":"bonus","value":1}]""");
        var applied = await accept.OkAsync("build", "apply", mini, build, operations);
        File.WriteAllText(build, applied.GetProperty("document").GetRawText());
        var context = Path.Combine(root, "accept-context.json");
        File.WriteAllText(context, applied.GetProperty("context").GetRawText());
        Assert.Equal(11, (await accept.OkAsync("build", "resolve", mini, build, "--context", context)).GetProperty("stats").GetProperty("armor").GetDouble());
        var code = (await accept.OkAsync("build", "share", mini, build)).GetProperty("code").GetString()!;
        await accept.OkAsync("share", "convert", mini, code);
        var empty = Path.Combine(root, "accept-empty.json");
        File.WriteAllText(empty, created.GetRawText());
        Assert.Equal(1, (await accept.OkAsync("build", "compare", mini, build, empty, "--context", context)).GetProperty("changed").GetInt32());

        // The committed pack: ranking and simulation.
        var (weapon, weaponContext) = await WriteDivisionWeaponBuildAsync();
        Assert.NotEmpty((await accept.OkAsync("build", "rank", CommittedDivision, weapon, "primary_weapon", "equipped_weapon_damage", "--context", weaponContext))
            .GetProperty("upgrades").EnumerateArray());
        Assert.True((await accept.OkAsync("build", "simulate", CommittedDivision, weapon, "--context", weaponContext)).GetProperty("weaponDps").GetProperty("mean").GetDouble() > 0);

        // The library.
        var saved = await accept.OkAsync("library", "save", build, "--library", Library);
        var id = saved.GetProperty("id").GetString()!;
        Assert.Single((await accept.OkAsync("library", "list", "--library", Library)).GetProperty("entries").EnumerateArray());
        var version = (await accept.OkAsync("library", "load", id, "--library", Library)).GetProperty("version").GetString()!;
        await accept.OkAsync("library", "delete", id, "--expected-version", version, "--library", Library);

        // A live window.
        var (host, session) = await StartSessionAsync();
        // The Division build above reused operations.json, so the session gets its own batch.
        var liveOperations = Path.Combine(root, "live-operations.json");
        File.WriteAllText(liveOperations, """[{"kind":"context","id":"bonus","value":1}]""");
        await using (host)
        {
            Assert.Single((await accept.OkAsync("session", "list", "--session-dir", Sessions)).GetProperty("sessions").EnumerateArray());
            Assert.Equal(0, (await accept.OkAsync("session", "inspect", "--session-dir", Sessions)).GetProperty("revision").GetInt64());
            await accept.OkAsync("session", "apply", "--expected-revision", "0", "--operations", liveOperations, "--session-dir", Sessions);
            Assert.Equal(11, (await accept.OkAsync("session", "resolve", "--session-dir", Sessions)).GetProperty("stats").GetProperty("armor").GetDouble());
            await accept.OkAsync("session", "undo", "--expected-revision", "1", "--session-dir", Sessions);
            await accept.OkAsync("session", "redo", "--expected-revision", session.Snapshot.Revision.ToString(CultureInfo.InvariantCulture), "--session-dir", Sessions);
            // Undo and redo move through history rather than adding to it: the opening state and the one edit.
            Assert.Equal(2, (await accept.OkAsync("session", "history", "--session-dir", Sessions)).GetProperty("history").GetArrayLength());
            // A stale edit is the one failure that still carries a result.
            var stale = await accept.RunAsync("session", "apply", "--expected-revision", "0", "--operations", liveOperations, "--session-dir", Sessions);
            Assert.Equal(CliApp.StaleRevision, stale.ExitCode);
        }

        // Failures share the envelope too.
        Assert.Equal(CliApp.UsageError, (await accept.RunAsync("frobnicate")).ExitCode);
        Assert.Equal(CliApp.IoFailure, (await accept.RunAsync("build", "resolve", mini, Path.Combine(root, "not-there.json"))).ExitCode);

        var advertised = described.GetProperty("commands").EnumerateArray().Select(grammar => Key(grammar.GetString()!.Split(' '))).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(advertised.Except(accept.Exercised));
    }

    [Fact]
    public async Task CancellationAnswersInJsonWithExit130()
    {
        var (weapon, weaponContext) = await WriteDivisionWeaponBuildAsync();
        var options = WriteSimulateOptions("""{"mode":"random","iterations":10000,"duration":600}""");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var timer = Stopwatch.StartNew();
        var exit = await CliApp.RunAsync(["build", "simulate", CommittedDivision, weapon, "--options", options, "--context", weaponContext], output, error, null, cancellation.Token);
        Assert.Equal(CliApp.Cancelled, exit);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"Cancelling took {timer.Elapsed}");
        var run = new Run(exit, output.ToString(), error.ToString());
        Assert.Equal([("error", "cancelled")], Diagnostics(run, false));
    }

    [Fact]
    public async Task CancellingASessionWaitStopsWaiting()
    {
        var (host, _) = await StartSessionAsync();
        await using var _1 = host;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var output = new StringWriter();
        var timer = Stopwatch.StartNew();
        var exit = await CliApp.RunAsync(["session", "inspect", "--wait", "30000", "--since-revision", "0", "--session-dir", Sessions], output, TextWriter.Null, null, cancellation.Token);
        Assert.Equal(CliApp.Cancelled, exit);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Cancelling took {timer.Elapsed}");
        // The window keeps serving after a client gives up.
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session-dir", Sessions)).ExitCode);
    }

    [Fact]
    public async Task AnEditCancelledBeforeItIsSentChangesNothing()
    {
        var (host, session) = await StartSessionAsync();
        await using var _1 = host;
        var operations = WriteOperations("""[{"kind":"name","text":"Never"}]""");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new StringWriter();
        var exit = await CliApp.RunAsync(["session", "apply", "--expected-revision", "0", "--operations", operations, "--session-dir", Sessions], output, TextWriter.Null, null, cancelled.Token);
        Assert.Equal(CliApp.Cancelled, exit);
        Assert.Equal(0, session.Snapshot.Revision);
    }

    [Fact]
    public async Task ARefusedSessionRequestCarriesNoResultOrNullFields()
    {
        var (host, _) = await StartSessionAsync();
        await using var _1 = host;
        var run = await RunAsync("session", "undo", "--expected-revision", "0", "--session-dir", Sessions);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "nothing-to-undo");
        Assert.False(run.Json.TryGetProperty("result", out _));
        Assert.False(run.Json.TryGetProperty("operationErrors", out _));
        Assert.DoesNotContain("null", run.Output, StringComparison.Ordinal);
    }
}
