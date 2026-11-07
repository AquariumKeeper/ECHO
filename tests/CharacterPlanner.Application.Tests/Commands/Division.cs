using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Application.Tests.Commands;

/// <summary>The committed Division 2 pack, its applier and the frozen fixtures the operation tests read.</summary>
internal static class Division
{
    public static CompiledPackExpressions Pack { get; } = Load();
    /// <summary>Accepts party share codes in either format, as a composition root that imports testers' Go-era codes would.</summary>
    public static OperationApplier Applier { get; } = new(Pack, code => ShareCodeReader.Read(code, Pack.Pack.LegacyShareOrder).Build);
    public static GamePack Rules => Pack.Pack;

    public static readonly string[] Builds = ["foundry-shield", "hard-wired-skills", "party-buffs", "prototype-gear", "shotgun-close", "striker-rifle"];

    private static CompiledPackExpressions Load()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");
        var files = Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
        var result = PackValidator.Load(files);
        Assert.True(result.Compiled is not null && result.Diagnostics.IsEmpty, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Compiled!;
    }

    public static byte[] Bytes(params string[] path) => File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, .. path]));

    public static JsonElement Json(params string[] path)
    {
        using var document = JsonDocument.Parse(Bytes(path));
        return document.RootElement.Clone();
    }

    /// <summary>A frozen v1 document (the {"schemaVersion", "build"} object) read into a snapshot.</summary>
    public static BuildSnapshot Decode(JsonElement document) => BuildDocumentJson.Decode(JsonSerializer.SerializeToUtf8Bytes(document)).Build;

    public static BuildSnapshot Empty() => BuildSnapshot.Create(Rules);

    public static PlannerState State(BuildSnapshot? build = null) => PlannerState.Of(build ?? Empty());

    public static ImmutableArray<BuildOperation> Read(string json)
    {
        var read = OperationJson.Read(json);
        Assert.True(read.Succeeded, string.Join(Environment.NewLine, read.Errors));
        return read.Operations;
    }

    public static OperationOutcome Apply(PlannerState state, params BuildOperation[] operations) => Applier.Apply(state, [.. operations]);

    public static OperationOutcome Apply(BuildSnapshot build, params BuildOperation[] operations) => Apply(PlannerState.Of(build), operations);

    /// <summary>Applies operations that must all succeed and returns the new state.</summary>
    public static PlannerState Applied(PlannerState state, params BuildOperation[] operations)
    {
        var outcome = Apply(state, operations);
        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Errors));
        return outcome.State;
    }

    public static BuildSnapshot Applied(BuildSnapshot build, params BuildOperation[] operations) => Applied(PlannerState.Of(build), operations).Build;

    /// <summary>Applies one operation that must fail, checks atomicity, and returns its error.</summary>
    public static OperationError Rejected(PlannerState state, params BuildOperation[] operations)
    {
        var outcome = Apply(state, operations);
        Assert.False(outcome.Succeeded);
        Assert.Same(state, outcome.State);
        Assert.Empty(outcome.Changes);
        return Assert.Single(outcome.Errors);
    }

    public static OperationError Rejected(BuildSnapshot build, params BuildOperation[] operations) => Rejected(PlannerState.Of(build), operations);

    /// <summary>A build with a Striker mask (multi-choice slots, a rolled gear mod) and chest (a single-choice talent).</summary>
    public static BuildSnapshot Geared() => Applied(Empty(),
        new EquipmentOperation("mask", "mask_striker_s_battlegear_mask"),
        new EquipmentOperation("chest", "chest_striker_s_battlegear_chest"),
        new ActiveSkillOperation("skill_slot_1", "skill_assault_turret"),
        new SpecializationOperation("gunner"));
}
