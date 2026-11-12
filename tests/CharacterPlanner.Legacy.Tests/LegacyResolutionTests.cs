using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Legacy.Documents;
using Xunit.Sdk;
using V1 = CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>
/// Runs the frozen v1 resolution corpora against converted v2 packs. Calculated results are the
/// contract: stats, breakdowns with provenance and order, sets, previews and build outputs.
/// Diagnostics compare by severity, code, subject and target; their wording is the app's own.
/// The Division fixtures' per-weapon stats are compared by <see cref="WeaponStatsMatchFrozenCli"/>.
/// Response fields that are not compared, and why:
/// <list type="bullet">
/// <item>apiVersion, command, ok, pack and error: the v1 CLI envelope; the CLI contract is the app's own (ADR 0002). ok and error follow from the compared exit code.</item>
/// <item>document and context: echoes of the inputs.</item>
/// <item>diagnostics[].message: wording is the app's own.</item>
/// <item>result.primary, points and skillPoints: outputs of removed features (primary stats, allocation and passive skills).</item>
/// <item>result.names: a display-name lookup over the pack, not a calculated result.</item>
/// <item>result.assumptions: the CLI's description of the assumption inputs, which v2 replaces with typed assumptions read from the pack.</item>
/// <item>result.unconditional: the CLI's what-if view, the stats re-resolved with the assumptions in reset cleared; not a resolver output.</item>
/// </list>
/// </summary>
public sealed class LegacyResolutionTests
{
    // The Division 2 cases run on the committed v2 pack through the strict reader; the converter
    // tests preserve the conversion reference; producer delivery tests guard accepted metadata differences.
    private static readonly CompiledPackExpressions Division = LegacyPacks.LoadV2(Path.Combine("packs", "division-2"));
    // R30 (adopted 2026-11-01 with the Rebalance cap candidate): weapon-talent burn and bleed tick damage.
    // Toxic Delivery's total-damage stat (2026-11-02) is likewise new since the frozen fixtures, and nothing reads it yet.
    // Slotted's colour counts, the weapon falloff floor (R42, read only by distance fights) and Doctor Home's kit preview (R43)
    // came later the same day.
    private static readonly ImmutableArray<string> AddedStats = ["status_tick_skill_curve", "burn_tick_damage", "bleed_tick_damage", "toxic_delivery_total_damage",
        "slotted_offensive_count", "slotted_defensive_count", "slotted_skill_count", "weapon_falloff_floor", "doctor_home_kit_repair_percent", "afterburn_detonation_damage", "apex_protocol_four_piece", "skill_overcharge", "overcharged_skill_tier", "overcharged_shield_tier",
        // Plague of the Outcasts' debuff total (R75, 2026-11-08), read only by fights.
        "plague_of_the_outcasts_debuff_damage",
        // The specialization kit bonus and the three kit values that read it (R85, 2026-11-08); without Efficient they're the old constants.
        "specialization_kit_bonus", "gunner_hardened_armor_kits_bonus_armor", "demolitionist_stimulant_patch_duration", "sharpshooter_emergency_cleanse_duration",
        // Regicide's blast damage (R88, 2026-11-09), read only by fights with an enemy spacing.
        "regicide_blast_damage"];
    private sealed record Suite(byte[] PackBytes, int Count);
    private static readonly Dictionary<string, Suite> Suites = new(StringComparer.Ordinal)
    {
        ["resolution"] = LoadSuite("resolution", 14, Path.Combine("packs", "ashen-vale.json")),
        ["equipment-resolution"] = LoadSuite("equipment-resolution", 21),
        ["upgrade-resolution"] = LoadSuite("upgrade-resolution", 21),
        ["prototype-resolution"] = LoadSuite("prototype-resolution", 23),
        ["talent-copy-resolution"] = LoadSuite("talent-copy-resolution", 19),
        ["specialization-resolution"] = LoadSuite("specialization-resolution", 18),
        ["active-skill-resolution"] = LoadSuite("active-skill-resolution", 20),
        ["skill-mod-resolution"] = LoadSuite("skill-mod-resolution", 20),
        ["build-output-resolution"] = LoadSuite("build-output-resolution", 10),
        ["party-resolution"] = LoadSuite("party-resolution", 14)
    };

    private const string AshenVale = "Ashen Vale golden corpus (F07) retires: the pack depends on primary stats, allocations, level ranges, requirements and scenarios (ADR 0001)";
    private const string Requirements = "item requirements are removed; the requirement-gated item is dropped from the converted pack";

    /// <summary>A retired case's reason, and the comparisons it fails. Every other comparison must still match.</summary>
    private sealed record Retirement(string Reason, ImmutableArray<string> Fails);

    /// <summary>Cases that exercise removed features. Exactly these must fail, and only on their listed comparisons; anything else must pass.</summary>
    private static readonly ImmutableDictionary<string, Retirement> Retired = new Dictionary<string, Retirement>(StringComparer.Ordinal)
    {
        ["resolution/new"] = new(AshenVale, ["conversion"]),
        ["resolution/golden"] = new(AshenVale, ["conversion"]),
        ["resolution/inactive-requirements"] = new(AshenVale, ["conversion"]),
        ["resolution/inactive-slot-conflict"] = new(AshenVale, ["conversion"]),
        ["resolution/active-slot-conflict"] = new(AshenVale, ["conversion"]),
        ["resolution/unknown-item"] = new(AshenVale, ["conversion"]),
        ["resolution/wrong-slot"] = new(AshenVale, ["conversion"]),
        ["resolution/overspend-and-clamp"] = new(AshenVale, ["conversion"]),
        ["resolution/above-max-level"] = new(AshenVale, ["conversion"]),
        ["resolution/scenario-tags"] = new(AshenVale, ["conversion"]),
        ["resolution/unknown-scenarios-and-skills"] = new(AshenVale, ["conversion"]),
        ["resolution/active-weapon"] = new(AshenVale, ["conversion"]),
        ["resolution/two-piece-set"] = new(AshenVale, ["conversion"]),
        ["resolution/four-piece-buffed"] = new(AshenVale, ["conversion"]),
        ["upgrade-resolution/account-increased-bonus"] = new("uses the removed inc operation", ["stats", "breakdowns"]),
        ["upgrade-resolution/inactive-item"] = new(Requirements + " (it also occupied two slots)", ["diagnostics"]),
        ["upgrade-resolution/inactive-invalid-rank"] = new(Requirements + " (it also occupied two slots)", ["diagnostics"]),
        ["prototype-resolution/inactive-item"] = new(Requirements, ["diagnostics"]),
        ["talent-copy-resolution/inactive-source"] = new(Requirements, ["diagnostics"]),
        ["talent-copy-resolution/inactive-copier"] = new(Requirements, ["diagnostics"]),
        ["talent-copy-resolution/copier-gated"] = new("item effect gates on context keys other than the active weapon are removed", ["stats", "breakdowns"]),
        ["talent-copy-resolution/source-selected-target-gated"] = new("v2 has no weapon_slot gates on talent modifiers; the gated modifier is dropped", ["stats", "breakdowns"]),
        ["active-skill-resolution/clamped-preview-level"] = new("the level is fixed by the pack; level-out-of-range is removed", ["diagnostics"])
    }.ToImmutableDictionary(StringComparer.Ordinal);

    // Null when the converter rejects the suite's pack.
    private static readonly ConcurrentDictionary<(string Suite, long Level), CompiledPackExpressions?> Converted = new();

    private static Suite LoadSuite(string name, int count, string? packPath = null) =>
        new(LegacyPacks.Bytes(packPath ?? Path.Combine(name, "pack.json")), count);

    // Declared before Rows: static initializers run in textual order.
    private static readonly string[] DivisionFixtures = ["foundry-shield", "hard-wired-skills", "party-buffs", "prototype-gear", "shotgun-close", "striker-rifle"];

    private static readonly Dictionary<string, JsonElement> Rows = LoadCases().ToDictionary(row => (string)row[0], row => (JsonElement)row[1], StringComparer.Ordinal);

    public static IEnumerable<object[]> Cases() => Rows.Keys.Select(id => new object[] { id });
    public static IEnumerable<object[]> DivisionCases() => Rows.Keys.Where(id => id.StartsWith("division-fixtures/", StringComparison.Ordinal)).Select(id => new object[] { id });

    private const string FoldedSuite = "specialization-resolution";

    private static IEnumerable<object[]> LoadCases()
    {
        foreach (var suite in Suites.Keys)
        {
            using var corpus = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine(suite, "legacy-v1.json")));
            foreach (var row in corpus.RootElement.GetProperty("cases").EnumerateArray())
            { yield return [suite + "/" + row.GetProperty("id").GetString()!, suite == FoldedSuite ? FoldPassiveExpected(row) : row.Clone()]; }
        }
        foreach (var name in DivisionFixtures)
        {
            foreach (var mode in new[] { "resolve", "resolve-context" })
            {
                using var fixture = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("c0-builds", name, mode + ".json")));
                var response = fixture.RootElement.GetProperty("response");
                var context = mode == "resolve" ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(LegacyPacks.Bytes(Path.Combine("c0-builds", "context.json")))!;
                var row = JsonSerializer.SerializeToElement(new { document = response.GetProperty("document"), context, response, exitCode = fixture.RootElement.GetProperty("exitCode").GetInt32() });
                yield return ["division-fixtures/" + name + "/" + mode, row];
            }
        }
    }

    // The specialization corpus allocates the removed passive skill node "passive" (cost 0, flat +4
    // damage) in every case, and its contribution is always damage's last. The test pack folds it into
    // damage's formula (100 becomes 104); the expected side moves the same 4 from that contribution to
    // damage's base. Every final stat, including ehp = damage * 2 plus child's damage * 0.1, and every
    // other contribution and its order are unchanged. The removed unknown-scenario diagnostic is dropped.
    private const double Passive = 4;

    private static V1.GamePack FoldPassivePack(V1.GamePack pack)
    {
        var damage = pack.Stats.Single(stat => stat.ID == "damage");
        if (damage.Formula != "100" || pack.Skills is not [{ ID: "passive", Cost: 0, Modifiers: [{ Stat: "damage", Op: "flat", When: "", Value.Number: Passive, Value.Formula: null }] }])
        { throw new InvalidOperationException("The specialization pack's damage formula or passive skill changed; revisit the passive fold."); }
        return pack with { Stats = pack.Stats.Select(stat => stat.ID == "damage" ? stat with { Formula = "104" } : stat).ToImmutableArray() };
    }

    private static JsonElement FoldPassiveExpected(JsonElement row)
    {
        if (!row.GetProperty("document").GetProperty("build").GetProperty("allocatedSkills").TryGetProperty("passive", out _))
        { throw new InvalidOperationException($"Specialization case {row.GetProperty("id")} no longer allocates the passive; revisit the passive fold."); }
        var node = JsonNode.Parse(row.GetRawText())!;
        var response = node["response"]!;
        var diagnostics = response["diagnostics"]!.AsArray();
        foreach (var removed in diagnostics.Where(diagnostic => (string?)diagnostic!["code"] == "unknown-scenario").ToArray()) { diagnostics.Remove(removed); }
        if (response["result"]?["breakdowns"]?["damage"] is { } damage)
        {
            var contributions = damage["contributions"]!.AsArray();
            if (contributions[^1] is not { } last || (string?)last["source"]!["kind"] != "skill" || (string?)last["source"]!["id"] != "passive" || (double)last["value"]! != Passive
                || contributions.Count(contribution => (string?)contribution!["source"]!["kind"] == "skill") != 1)
            { throw new InvalidOperationException($"Specialization case {row.GetProperty("id")} has no single trailing passive contribution; revisit the passive fold."); }
            contributions.Remove(last);
            damage["base"] = (double)damage["base"]! + Passive;
        }
        return JsonSerializer.SerializeToElement(node);
    }

    [Fact]
    public void CorpusKeepsAcceptedReleasePackAndInputHashes()
    {
        using var manifest = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("pack-validation", "manifest.json")));
        foreach (var (suite, fixture) in Suites)
        {
            using var corpus = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine(suite, "legacy-v1.json")));
            var reference = corpus.RootElement.GetProperty("reference");
            Assert.Equal(manifest.RootElement.GetProperty("commit").GetString(), reference.GetProperty("commit").GetString());
            Assert.Equal(manifest.RootElement.GetProperty("zip").GetProperty("sha256").GetString(), reference.GetProperty("zipSha256").GetString());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixture.PackBytes)), reference.GetProperty("packSha256").GetString());
            var inputBytes = LegacyPacks.Bytes(Path.Combine(suite, "inputs.json"));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(inputBytes)), reference.GetProperty("inputsSha256").GetString());
            Assert.Equal(fixture.Count, corpus.RootElement.GetProperty("cases").GetArrayLength());
        }
    }

    [Fact]
    public void RetiredCasesExistAndNoDivisionCaseIsRetired()
    {
        Assert.All(Retired.Keys, id => Assert.True(Rows.ContainsKey(id), $"Retired case {id} is not in the corpus"));
        Assert.DoesNotContain(Retired.Keys, id => id.StartsWith("division-fixtures/", StringComparison.Ordinal));
        Assert.DoesNotContain(Retired.Keys, id => id.StartsWith(FoldedSuite + "/", StringComparison.Ordinal));
        Assert.All(Retired.Values, retirement => Assert.NotEmpty(retirement.Fails));
        Assert.Equal(12, Rows.Keys.Count(id => id.StartsWith("division-fixtures/", StringComparison.Ordinal)));
    }

    [Fact]
    public void AshenValeIsRejectedByTheConverter()
    {
        var conversion = V1.PackV1Converter.Convert(LegacyPacks.Read(Suites["resolution"].PackBytes));
        Assert.Null(conversion.Pack);
        Assert.Contains(conversion.Problems, problem => problem.Path.EndsWith(".kind", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void BuildResolutionMatchesFrozenCli(string id)
    {
        var comparisons = Comparisons(id);
        if (!Retired.TryGetValue(id, out var retirement))
        {
            foreach (var (_, compare) in comparisons) { compare(); }
            return;
        }
        // A retired case may fail only on a comparison, never by crashing, and only where it says.
        var failures = comparisons.Select(comparison => (comparison.Part, Error: Record.Exception(comparison.Compare))).Where(result => result.Error is not null).ToArray();
        Assert.True(failures.Length > 0, $"Retired case {id} ({retirement.Reason}) now matches; remove it from the retired table");
        foreach (var (part, error) in failures)
        { Assert.True(error is IAssertionException, $"Retired case {id} {part} failed with {error!.GetType().Name} rather than a mismatch: {error}"); }
        Assert.True(retirement.Fails.SequenceEqual(failures.Select(failure => failure.Part)),
            $"Retired case {id} fails [{string.Join(", ", failures.Select(failure => failure.Part))}], expected [{string.Join(", ", retirement.Fails)}]");
    }

    /// <summary>
    /// The frozen per-weapon stats resolve each equipped weapon as if it were the active one. Each is
    /// re-resolved with weapon_slot set to that weapon's slot, keeping the fixture's other context.
    /// </summary>
    [Theory]
    [MemberData(nameof(DivisionCases))]
    public void WeaponStatsMatchFrozenCli(string id)
    {
        var row = Rows[id];
        var import = BuildDocumentJson.Decode(Encoding.UTF8.GetBytes(row.GetProperty("document").GetRawText()));
        var context = Context(row);
        var weapons = row.GetProperty("response").GetProperty("result").GetProperty("weapons").EnumerateArray().ToArray();
        Assert.Equal(Division.Pack.Slots.Where(slot => slot.Weapon && import.Build.Equipment.GetValueOrDefault(slot.ID, "").Length > 0).Select(slot => slot.ID),
            weapons.Select(weapon => weapon.GetProperty("slot").GetString()));
        foreach (var weapon in weapons)
        {
            var slot = weapon.GetProperty("slot").GetString()!;
            var itemId = weapon.GetProperty("itemId").GetString()!;
            Assert.Equal(itemId, import.Build.Equipment[slot]);
            Assert.Equal(weapon.GetProperty("name").GetString(), Division.Pack.Items.Single(item => item.ID == itemId).Name);
            Assert.Equal(weapon.GetProperty("active").GetBoolean(), context.GetValueOrDefault(GamePack.WeaponSlotSelector) == slot);
            var actual = BuildResolver.Resolve(Division, import.Build, context.SetItem(GamePack.WeaponSlotSelector, slot));
            Assert.DoesNotContain(actual.Diagnostics, diagnostic => diagnostic.Severity == "error");
            var stats = weapon.GetProperty("stats").EnumerateObject().ToArray();
            Assert.NotEmpty(stats);
            foreach (var stat in stats)
            {
                Assert.True(actual.Calculation.Stats.TryGetValue(stat.Name, out var value), $"{slot} has no {stat.Name}");
                Close(stat.Value.GetDouble(), value);
            }
        }
    }

    private static ImmutableDictionary<string, string> Context(JsonElement row) =>
        row.GetProperty("context").EnumerateObject().ToImmutableDictionary(pair => pair.Name, pair => pair.Value.GetString()!);

    /// <summary>
    /// The case's comparisons in order, each named for the retired table. Decoding, conversion and
    /// resolution run first, so a crash there fails the test whether or not the case is retired.
    /// </summary>
    private static List<(string Part, Action Compare)> Comparisons(string id)
    {
        var row = Rows[id];
        var import = BuildDocumentJson.Decode(Encoding.UTF8.GetBytes(row.GetProperty("document").GetRawText()));
        var suite = id.Split('/')[0];
        var division = suite == "division-fixtures";
        var compiled = division ? Division : Converted.GetOrAdd((suite, import.Level), key => ConvertSuite(key.Suite, key.Level));
        if (compiled is null)
        { return [("conversion", () => Assert.Fail($"The converter rejects the {suite} pack"))]; }
        ImmutableHashSet<string> constants = division ? [] : SyntheticV1.PrimaryStats(LegacyPacks.Read(Suites[suite].PackBytes));
        var actual = BuildResolver.Resolve(compiled, import.Build, Context(row));
        var response = row.GetProperty("response");
        var comparisons = new List<(string Part, Action Compare)>();
        void Add(string part, Action compare) => comparisons.Add((part, compare));
        Add("diagnostics", () =>
        {
            var expectedDiagnostics = response.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => (
                diagnostic.GetProperty("severity").GetString()!, diagnostic.GetProperty("code").GetString()!,
                OptionalString(diagnostic, "subject"), OptionalString(diagnostic, "target"))).ToArray();
            Assert.Equal(expectedDiagnostics, actual.Diagnostics.Select(diagnostic => diagnostic.Key()));
            Assert.Equal(actual.Diagnostics.Any(diagnostic => diagnostic.Severity == "error") ? 3 : 0, row.GetProperty("exitCode").GetInt32());
        });
        // The CLI intentionally withholds computed results for invalid builds.
        if (!response.TryGetProperty("result", out var result)) { return comparisons; }
        var stats = result.GetProperty("stats");
        Add("stats", () =>
        {
            // Stats added to the committed pack after the freeze on purpose (ADR 0002, ledger P01) must be present and are
            // compared by their own tests; every frozen stat still matches exactly.
            if (division) { Assert.All(AddedStats, id => Assert.True(actual.Calculation.Stats.ContainsKey(id), $"Missing added stat {id}")); }
            CheckValues(stats, division ? actual.Calculation.Stats.RemoveRange(AddedStats) : actual.Calculation.Stats);
        });
        Add("buildOutputs", () =>
        {
            var buildOutputs = result.TryGetProperty("buildOutputs", out var expectedBuildOutputs) ? expectedBuildOutputs.EnumerateArray().ToArray() : [];
            Assert.Equal(buildOutputs.Length, actual.BuildOutputs.Length);
            for (var index = 0; index < buildOutputs.Length; index++)
            {
                var expected = buildOutputs[index];
                var output = actual.BuildOutputs[index];
                Assert.Equal(expected.GetProperty("id").GetString(), output.Id);
                Assert.Equal(expected.GetProperty("name").GetString(), output.Name);
                Assert.Equal(OptionalString(expected, "description"), output.Description);
                Assert.Equal(expected.GetProperty("format").GetString(), output.Format);
                var shift = division && output.Id is "build_dps" or "build_time_to_kill" ? SkillModDelta.BuildDpsShift(result.GetProperty("activeSkillPreviews"), actual.ActiveSkillPreviews) : 0;
                if (output.Id == "build_dps") { Assert.Equal(expected.GetProperty("value").GetDouble() + shift, output.Value, 6); }
                // Time to kill divides by that same total, so a shift can't be added to it. The formula has its own tests.
                else if (shift == 0) { Close(expected.GetProperty("value").GetDouble(), output.Value); }
            }
        });
        Add("previews", () =>
        {
            var previews = result.GetProperty("activeSkillPreviews");
            Assert.Equal(previews.EnumerateObject().Count(), actual.ActiveSkillPreviews.Count);
            foreach (var (slot, preview) in actual.ActiveSkillPreviews)
            {
                var expected = previews.GetProperty(slot);
                Assert.Equal(expected.GetProperty("skillId").GetString(), preview.SkillId);
                var outputs = expected.GetProperty("outputs").EnumerateArray().ToArray();
                var compared = division ? [.. preview.Outputs.Where(output => !SkillModDelta.IsAdded(preview.SkillId, output.Id))] : preview.Outputs;
                Assert.Equal(outputs.Length, compared.Length);
                for (var index = 0; index < outputs.Length; index++)
                {
                    var output = outputs[index];
                    var value = division ? ShieldModDelta.Revert(preview.SkillId, compared[index]) : compared[index];
                    Assert.Equal(output.GetProperty("id").GetString(), value.Id);
                    Assert.Equal(output.GetProperty("name").GetString(), value.Name);
                    var reviewed = division && SkillModDelta.Covers(preview.SkillId);
                    if (!division || !SkillModDelta.Reworded(preview.SkillId)) { Assert.Equal(OptionalString(output, "description"), value.Description); }
                    Assert.Equal(output.GetProperty("format").GetString(), value.Format);
                    if (!reviewed) { Assert.Equal(division ? output.GetProperty("formula").GetString() : SyntheticV1.Expression(output.GetProperty("formula").GetString()!), division ? SkillModDelta.FrozenTier(value.Formula) : value.Formula); }
                    Assert.Equal(division ? OptionalString(output, "when") : SyntheticV1.Expression(OptionalString(output, "when")), value.When);
                    Assert.Equal(OptionalString(output, "kind"), value.Kind);
                    var inputs = division ? SkillModDelta.FrozenTierInputs(value.Inputs, actual.Calculation.Stats) : value.Inputs;
                    CheckValues(output.GetProperty("inputs"), reviewed ? SkillModDelta.FrozenInputs(output.GetProperty("inputs"), inputs) : inputs);
                    if (!(division && SkillModDelta.Moved(preview.SkillId, value.Id))) { Close(output.GetProperty("value").GetDouble(), value.Value); }
                }
            }
        });
        Add("breakdowns", () =>
        {
            var breakdowns = result.GetProperty("breakdowns");
            foreach (var (stat, breakdown) in actual.Calculation.Breakdowns)
            {
                if (division && AddedStats.Contains(stat)) { continue; }
                if (!breakdowns.TryGetProperty(stat, out var expected))
                {
                    // v1 primary stats had no breakdown; their converted constants must be trivial.
                    Assert.Contains(stat, constants);
                    Close(stats.GetProperty(stat).GetDouble(), breakdown.Base);
                    Close(breakdown.Base, breakdown.Final);
                    Assert.Empty(breakdown.Contributions);
                    continue;
                }
                Close(expected.GetProperty("base").GetDouble(), breakdown.Base);
                Close(expected.GetProperty("final").GetDouble(), breakdown.Final);
                Assert.Equal(expected.GetProperty("clamped").GetBoolean(), breakdown.Clamped);
                var contributions = expected.GetProperty("contributions").EnumerateArray().ToArray();
                Assert.Equal(contributions.Length, breakdown.Contributions.Length);
                for (var index = 0; index < contributions.Length; index++)
                {
                    var contribution = contributions[index];
                    var value = breakdown.Contributions[index];
                    var owner = contribution.GetProperty("source");
                    Assert.Equal(new ModifierSource(owner.GetProperty("kind").GetString()!, owner.GetProperty("id").GetString()!, owner.GetProperty("name").GetString()!,
                        owner.TryGetProperty("rank", out var rank) ? rank.GetInt32() : 0, OptionalString(owner, "slotId")), value.Source);
                    Assert.Equal(contribution.GetProperty("op").GetString(), value.Op);
                    // Buckets are removed; a bucketed contribution must not hide here.
                    Assert.Equal("", OptionalString(contribution, "bucket"));
                    Close(contribution.GetProperty("value").GetDouble(), value.Value);
                }
            }
            Assert.Equal(breakdowns.EnumerateObject().Count(), actual.Calculation.Breakdowns.Keys.Count(stat => !constants.Contains(stat) && !(division && AddedStats.Contains(stat))));
        });
        Add("sets", () =>
        {
            var sets = result.TryGetProperty("sets", out var expectedSets) ? expectedSets.EnumerateArray().ToArray() : [];
            Assert.Equal(sets.Length, actual.ActiveSets.Length);
            for (var index = 0; index < sets.Length; index++)
            {
                var expected = sets[index];
                var set = actual.ActiveSets[index];
                Assert.Equal(expected.GetProperty("id").GetString(), set.SetId);
                Assert.Equal(expected.GetProperty("name").GetString(), set.Name);
                Assert.Equal(expected.GetProperty("pieces").GetInt64(), set.Pieces);
                // v1 omitted a zero bonusPieces.
                Assert.Equal(expected.TryGetProperty("bonusPieces", out var bonusPieces) ? bonusPieces.GetInt64() : 0, set.BonusPieces);
                // The thresholds are the set's bonus piece counts, in pack order.
                Assert.Equal(expected.GetProperty("thresholds").EnumerateArray().Select(value => value.GetInt64()),
                    compiled.Pack.Sets.Single(definition => definition.ID == set.SetId).Bonuses.Select(bonus => bonus.Pieces));
                Assert.Equal(expected.GetProperty("activeBonuses").EnumerateArray().Select(value => value.GetInt64()), set.AppliedBonuses);
            }
        });
        return comparisons;
    }

    private static CompiledPackExpressions? ConvertSuite(string suite, long level)
    {
        var source = LegacyPacks.Read(Suites[suite].PackBytes);
        var pack = SyntheticV1.Simplify(suite == FoldedSuite ? FoldPassivePack(source) : source, level);
        var conversion = V1.PackV1Converter.Convert(pack);
        return conversion.Pack is null ? null : LegacyPacks.Compile(conversion.Pack);
    }

    private static string OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString()! : "";
    private static void CheckValues(JsonElement expected, ImmutableDictionary<string, double> actual)
    {
        Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var pair in expected.EnumerateObject()) { Close(pair.Value.GetDouble(), actual[pair.Name]); }
    }
    // Raw double comparison for calculated stats, formula bases and contributions:
    // absolute 1e-12 plus relative 1e-12. IDs, order, bounds and diagnostics are exact.
    private static void Close(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-12 + Math.Abs(expected) * 1e-12, $"Expected {expected:R}, actual {actual:R}");
}
