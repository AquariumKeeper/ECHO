using System.Reflection;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    // Full build commands verify artwork, so these read the committed pack rather than the artwork-free test copy.
    private static readonly string CommittedDivision = Path.Combine(typeof(CliAppTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "CommittedDataRoot").Value!, "packs", "division-2");

    /// <summary>A Division 2 build with an equipped ACR, and the context that makes it the active weapon against targets.</summary>
    private async Task<(string Build, string Context)> WriteDivisionWeaponBuildAsync()
    {
        var created = await RunAsync("build", "new", CommittedDivision);
        var build = Path.Combine(root, "division-build.json");
        File.WriteAllText(build, created.Json.GetProperty("result").GetRawText());
        var operations = WriteOperations("""
            [{"kind":"equipment","slot":"primary_weapon","id":"primary_weapon_acr"},
             {"kind":"context","id":"weapon_slot","text":"primary_weapon"},
             {"kind":"context","id":"enemy_target","value":1}]
            """);
        var applied = await RunAsync("build", "apply", CommittedDivision, build, operations);
        Assert.Equal(CliApp.Success, applied.ExitCode);
        File.WriteAllText(build, applied.Json.GetProperty("result").GetProperty("document").GetRawText());
        var context = Path.Combine(root, "division-context.json");
        File.WriteAllText(context, applied.Json.GetProperty("result").GetProperty("context").GetRawText());
        return (build, context);
    }

    private string WriteSimulateOptions(string text)
    {
        var path = Path.Combine(root, "options.json");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task SimulateRunsTheSharedEngineAndReturnsStableOrderedResults()
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var options = WriteSimulateOptions("""{"mode":"random","iterations":6,"duration":20,"seed":9,"captureFirstFightEvents":true,"retargetDelay":0.5}""");
        var before = FilesIn(root);
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options", options, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Equal("", run.Error);
        Assert.DoesNotContain(Diagnostics(run, true), diagnostic => diagnostic.Severity == "error");
        var result = run.Json.GetProperty("result");
        Assert.Equal("d2p-static-target-v1/d2p-splitmix64-v1", result.GetProperty("contract").GetString());
        Assert.Equal("random", result.GetProperty("mode").GetString());
        Assert.Equal(6, result.GetProperty("iterations").GetInt32());
        Assert.Equal(9UL, result.GetProperty("seed").GetUInt64());
        Assert.True(result.GetProperty("targets").GetBoolean());
        Assert.Equal(20, result.GetProperty("timeline").GetArrayLength());
        var events = result.GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(events, entry => entry.GetProperty("kind").GetString() == "shot" && entry.GetProperty("amount").ValueKind == JsonValueKind.Number);
        Assert.All(events.Where(entry => entry.GetProperty("kind").GetString() == "targetArrived"), entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("amount").ValueKind));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), FilesIn(root).Keys.Order(StringComparer.Ordinal));

        // The same engine and inputs the desktop uses give the same numbers.
        var loaded = await PackFileLoader.LoadAsync(CommittedDivision, TestContext.Current.CancellationToken);
        var compiled = Assert.IsType<CompiledPackExpressions>(loaded.Validation.Compiled);
        var document = BuildDocument.Decode(File.ReadAllBytes(build));
        var assumptions = JsonDocument.Parse(File.ReadAllText(context)).RootElement.EnumerateObject()
            .ToDictionary(pair => pair.Name, pair => pair.Value.ValueKind == JsonValueKind.Number ? pair.Value.GetRawText() : pair.Value.GetString()!);
        var direct = DummyWeaponSimulator.RunAgainstTargets(compiled, document, System.Collections.Immutable.ImmutableDictionary.CreateRange(assumptions),
            new() { Mode = SimulationMode.Random, Iterations = 6, Duration = 20, Seed = 9, CaptureFirstFightEvents = true }, 0.5, TestContext.Current.CancellationToken);
        Assert.Equal(direct.WeaponDps.Mean, result.GetProperty("weaponDps").GetProperty("mean").GetDouble());
        Assert.Equal(direct.Kills, result.GetProperty("kills").GetDouble());

        var repeated = await RunAsync("build", "simulate", CommittedDivision, build, "--options", options, "--context", context);
        Assert.Equal(run.Output, repeated.Output);
    }

    [Fact]
    public async Task SimulateWithoutOptionsUsesTheDefaultAverageFight()
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var result = run.Json.GetProperty("result");
        Assert.Equal("average", result.GetProperty("mode").GetString());
        Assert.Equal(60, result.GetProperty("duration").GetDouble());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("events").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("incoming").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("statusDps").ValueKind);
    }

    [Theory]
    [InlineData("""[]""", "must be a JSON object")]
    [InlineData("""{"duration":0}""", "'duration'")]
    [InlineData("""{"duration":601}""", "'duration'")]
    [InlineData("""{"iterations":0}""", "'iterations'")]
    [InlineData("""{"iterations":1.5}""", "'iterations'")]
    [InlineData("""{"seed":-1}""", "'seed'")]
    [InlineData("""{"mode":"Random"}""", "'mode'")]
    [InlineData("""{"includeSkills":"yes"}""", "'includeSkills'")]
    [InlineData("""{"approximatePreservationOverlap":"yes"}""", "'approximatePreservationOverlap'")]
    [InlineData("""{"distance":-1}""", "'distance'")]
    [InlineData("""{"retargetDelay":61}""", "'retargetDelay'")]
    [InlineData("""{"pickupDelay":61}""", "'pickupDelay'")]
    [InlineData("""{"pickupPercent":101}""", "'pickupPercent'")]
    [InlineData("""{"pickupChance":0.5}""", "Unknown option 'pickupChance'")]
    [InlineData("""{"durations":30}""", "Unknown option 'durations'")]
    [InlineData("""{"seed":1,"seed":2}""", "repeat 'seed'")]
    [InlineData("""{"rotation":{"mode":"afterKill"}}""", "needs a secondSlot")]
    [InlineData("""{"rotation":{"secondSlot":"secondary_weapon","swap":1}}""", "Unknown rotation option 'swap'")]
    [InlineData("""{"rotation":{"secondSlot":"primary_weapon"}}""", "is already the active weapon")]
    [InlineData("""{"rotation":{"secondSlot":"chest"}}""", "is not a weapon slot")]
    [InlineData("""{"rotation":{"secondSlot":"secondary_weapon"}}""", "has no weapon equipped")]
    [InlineData("""{"targets":false,"retargetDelay":5}""", "applies only with targets")]
    [InlineData("""{"targets":false,"clearKills":3}""", "'clearKills' applies only with targets")]
    [InlineData("""{"clearKills":0}""", "'clearKills'")]
    [InlineData("""{"targetSequence":[]}""", "'targetSequence' needs a list")]
    [InlineData("""{"targetSequence":"2,5"}""", "'targetSequence' needs a list")]
    [InlineData("""{"targetSequence":[2,0]}""", "'targetSequence'")]
    [InlineData("""{"targets":false,"targetSequence":[2]}""", "'targetSequence' applies only with targets")]
    [InlineData("""{"targetDistances":[]}""", "'targetDistances' needs a list")]
    [InlineData("""{"targetDistances":[10,1001]}""", "'targetDistances'")]
    [InlineData("""{"targets":false,"targetDistances":[10]}""", "'targetDistances' applies only with targets")]
    [InlineData("""{"peekSeconds":0}""", "'peekSeconds'")]
    [InlineData("""{"focusSeconds":0}""", "'focusSeconds'")]
    [InlineData("""{"coverSeconds":61}""", "'coverSeconds'")]
    [InlineData("""{"peekHitPercent":101}""", "'peekHitPercent'")]
    [InlineData("""{"peekSeconds":2}""", "needs 'peekSeconds' and 'coverSeconds' together")]
    [InlineData("""{"peekHitPercent":25}""", "'peekHitPercent' needs a cover cycle")]
    [InlineData("""{"peekSeconds":2,"coverSeconds":1,"includeIncoming":true}""", "with 'includeIncoming' needs 'peekHitPercent'")]
    [InlineData("""{"enemiesAtOnce":1}""", "'enemiesAtOnce'")]
    [InlineData("""{"armorKits":0,"armorKitAtPercent":50}""", "'armorKits'")]
    [InlineData("""{"armorKits":11,"armorKitAtPercent":50}""", "'armorKits'")]
    [InlineData("""{"armorKits":3,"armorKitAtPercent":101}""", "'armorKitAtPercent'")]
    [InlineData("""{"armorKits":3}""", "'armorKits' and 'armorKitAtPercent' together")]
    [InlineData("""{"enemiesShrink":"yes"}""", "'enemiesShrink'")]
    [InlineData("""{"enemiesAtOnce":11}""", "'enemiesAtOnce'")]
    [InlineData("""{"mode":"\uD800"}""", "not valid Unicode")]
    [InlineData("""{"\uD800":1}""", "not valid Unicode")]
    public async Task InvalidSimulationOptionsExitThreeWithoutAResult(string options, string message)
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions(options), "--context", context);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.False(run.Json.TryGetProperty("result", out _));
        var diagnostic = run.Json.GetProperty("diagnostics").EnumerateArray().Single(entry => entry.GetProperty("severity").GetString() == "error");
        Assert.Equal("simulation-options-invalid", diagnostic.GetProperty("code").GetString());
        Assert.Contains(message, diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATargetSequenceRunsAndSaysSo()
    {
        // P10, mixed targets: normals (profile 2) then an elite (profile 5) on the committed pack, in driven fights.
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"seed":3,"mode":"random","iterations":3,"driveSimpleBuffs":true,"targetSequence":[2,2,5]}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Contains(run.Json.GetProperty("result").GetProperty("limitations").EnumerateArray(),
            note => note.GetString()!.StartsWith("Targets cycle through a sequence of 3 (profiles 2, 5)", StringComparison.Ordinal));
        var refused = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"targetSequence":[2,5]}"""), "--context", context);
        Assert.Equal(CliApp.InvalidInput, refused.ExitCode);
        Assert.Contains(Diagnostics(refused, false), diagnostic => diagnostic.Code == "target-sequence-unsupported");
    }

    [Fact]
    public async Task ClearKillsReportsTimeToClearBesideTheFight()
    {
        // P10: with targets, the time each fight takes to kill this many, and the share of fights that did.
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"seed":3,"clearKills":1,"iterations":5}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var result = run.Json.GetProperty("result");
        Assert.Equal(1, result.GetProperty("clearedFraction").GetDouble());
        Assert.True(result.GetProperty("timeToClear").GetProperty("mean").GetDouble() > 0);
        Assert.True(result.GetProperty("timeToKill").GetProperty("mean").GetDouble() > 0);
        var plain = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"seed":3}"""), "--context", context);
        Assert.Equal(JsonValueKind.Null, plain.Json.GetProperty("result").GetProperty("clearedFraction").ValueKind);
    }

    [Fact]
    public async Task ACoverCycleRunsAndSaysSo()
    {
        // ADR 0009: peeking from cover and ducking behind it, in driven fights.
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"seed":3,"mode":"random","iterations":3,"driveSimpleBuffs":true,"peekSeconds":3,"coverSeconds":1}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Contains(run.Json.GetProperty("result").GetProperty("limitations").EnumerateArray(),
            note => note.GetString()!.StartsWith("Assumed cover cycle: 3 s peeking from cover, then 1 s behind it (25% of this fight)", StringComparison.Ordinal));
        var refused = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"peekSeconds":3,"coverSeconds":1}"""), "--context", context);
        Assert.Equal(CliApp.InvalidInput, refused.ExitCode);
        Assert.Contains(Diagnostics(refused, false), diagnostic => diagnostic.Code == "cover-cycle-unsupported");
    }

    [Fact]
    public async Task SeveralEnemiesRunAndSaySo()
    {
        // ADR 0008: mixed profiles firing at once on the committed pack, with enemy hits at more than one damage.
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"seed":3,"mode":"random","iterations":2,"duration":20,"driveSimpleBuffs":true,"includeIncoming":true,"enemiesAtOnce":3,"targetSequence":[2,5]}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Contains(run.Json.GetProperty("result").GetProperty("limitations").EnumerateArray(),
            note => note.GetString()!.StartsWith("3 enemies fire at once", StringComparison.Ordinal));
        var shrinking = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"seed":3,"mode":"random","iterations":2,"duration":20,"driveSimpleBuffs":true,"includeIncoming":true,"enemiesAtOnce":3,"clearKills":5,"enemiesShrink":true}"""), "--context", context);
        Assert.Equal(CliApp.Success, shrinking.ExitCode);
        Assert.Contains(shrinking.Json.GetProperty("result").GetProperty("limitations").EnumerateArray(),
            note => note.GetString()!.Contains("There are 5 in all", StringComparison.Ordinal));
        var refused = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"mode":"random","driveSimpleBuffs":true,"enemiesAtOnce":3}"""), "--context", context);
        Assert.Equal(CliApp.InvalidInput, refused.ExitCode);
        Assert.Contains(Diagnostics(refused, false), diagnostic => diagnostic.Code == "enemies-at-once-unsupported");
    }

    [Fact]
    public async Task FocusTimeRunsThroughTheCli()
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"mode":"random","duration":10,"seed":1,"driveSimpleBuffs":true,"includeIncoming":true,"enemiesAtOnce":3,"focusSeconds":2}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Contains("Assumed shooting pattern: 2 s", run.Json.GetProperty("result").GetProperty("limitations").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArmorKitsRunOnTheCommittedPack()
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        // Kits need armor to repair.
        var armored = await RunAsync("build", "apply", CommittedDivision, build, WriteOperations("""
            [{"kind":"equipment","slot":"mask","id":"mask_badger_tuff_mask"},
             {"kind":"equipment","slot":"chest","id":"chest_badger_tuff_chest"},
             {"kind":"equipment","slot":"gloves","id":"gloves_badger_tuff_gloves"},
             {"kind":"equipment","slot":"holster","id":"holster_badger_tuff_holster"},
             {"kind":"equipment","slot":"knees","id":"knees_badger_tuff_kneepads"},
             {"kind":"context","id":"weapon_slot","text":"primary_weapon"},
             {"kind":"context","id":"enemy_target","value":1},
             {"kind":"context","id":"enemy_difficulty","value":1}]
            """));
        Assert.Equal(CliApp.Success, armored.ExitCode);
        File.WriteAllText(build, armored.Json.GetProperty("result").GetProperty("document").GetRawText());
        File.WriteAllText(context, armored.Json.GetProperty("result").GetProperty("context").GetRawText());
        var run = await RunAsync("build", "simulate", CommittedDivision, build, "--options",
            WriteSimulateOptions("""{"mode":"random","duration":120,"seed":1,"driveSimpleBuffs":true,"includeIncoming":true,"armorKits":3,"armorKitAtPercent":50}"""), "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.True(run.Json.GetProperty("result").GetProperty("incoming").GetProperty("armorKitsUsed").GetDouble() > 0);
    }

    [Fact]
    public async Task TheDeprecatedPreservationOverlapFlagIsAcceptedAndChangesNothing()
    {
        // Overlapping Preservation repairs always run independently since R41 (2026-11-02); a saved request with the flag still runs.
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var plain = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"seed":3}"""), "--context", context);
        var flagged = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"seed":3,"approximatePreservationOverlap":true}"""), "--context", context);
        Assert.Equal((CliApp.Success, CliApp.Success), (plain.ExitCode, flagged.ExitCode));
        Assert.Equal(plain.Json.GetProperty("result").GetRawText(), flagged.Json.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task EngineRefusalsAndMissingWeaponsExitThree()
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var averageIncoming = await RunAsync("build", "simulate", CommittedDivision, build, "--options", WriteSimulateOptions("""{"includeIncoming":true}"""), "--context", context);
        Assert.Equal(CliApp.InvalidInput, averageIncoming.ExitCode);
        Assert.Contains(Diagnostics(averageIncoming, false), diagnostic => diagnostic.Code == "incoming-mode-unsupported");
        // A refusal depends on the build, context and options together, so it names no single file.
        Assert.Equal("", averageIncoming.Json.GetProperty("diagnostics").EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "incoming-mode-unsupported").GetProperty("path").GetString());

        var created = await RunAsync("build", "new", CommittedDivision);
        var empty = Path.Combine(root, "empty.json");
        File.WriteAllText(empty, created.Json.GetProperty("result").GetRawText());
        var unarmed = await RunAsync("build", "simulate", CommittedDivision, empty);
        Assert.Equal(CliApp.InvalidInput, unarmed.ExitCode);
        Assert.Contains(Diagnostics(unarmed, false), diagnostic => diagnostic.Code == "weapon-required");
    }

    [Theory]
    [InlineData("--options")]
    [InlineData("--options", "a.json", "--options", "b.json")]
    [InlineData("--context", "a.json", "--context", "b.json")]
    [InlineData("--seed", "1")]
    public async Task MalformedSimulateArgumentsAreUsageErrors(params string[] rest)
    {
        var run = await RunAsync(["build", "simulate", CommittedDivision, "build.json", .. rest]);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        Assert.Contains("build simulate <pack-directory>", run.Error, StringComparison.Ordinal);
    }
}
