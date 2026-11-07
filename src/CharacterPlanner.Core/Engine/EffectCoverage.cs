using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record EffectChannel(string Kind, ImmutableArray<string> Sources, string Note);
public sealed record TalentEffectCoverage(string Slot, string Talent, string Name, bool Active,
    ImmutableArray<EffectChannel> Channels)
{
    /// <summary>Whether the pack gives the talent any modeled channel; a source audit alone models nothing.</summary>
    public bool Modeled => Channels.Any(channel => channel.Kind != EffectCoverage.SourceAudit);
    /// <summary>The producer's source-audit status, or "" when the talent is not audited.</summary>
    public string AuditStatus => Channels.FirstOrDefault(channel => channel.Kind == EffectCoverage.SourceAudit)?.Sources[0] ?? "";
    public string Description => (Active ? "" : "Weapon effects are inactive in this context; explicitly authored holstered modifiers may still apply. ")
        + (Modeled ? "" : EffectCoverage.NoModel + (Channels.IsEmpty ? "" : " "))
        + string.Join(" ", Channels.Select(channel => channel.Note));
}

/// <summary>Authored-channel evidence, not a certification of complete talent mechanics or combat uptime.</summary>
public sealed record EffectCoverageReport(ImmutableArray<TalentEffectCoverage> Talents, ImmutableDictionary<string, string> Inputs)
{
    public static EffectCoverageReport Empty { get; } = new([], ImmutableDictionary<string, string>.Empty);
    public ImmutableArray<PartyBuff> Party { get; init; } = [];
    public string Note => "Coverage inventories authored effect channels; it does not certify complete talent support or actual contribution.";
    public string InputBasis { get; init; } = "Fixed preview assumptions, not measured combat uptime.";
    public string Summary => Note + EffectCoverage.Omissions(Talents);
}

/// <summary>Pure, on-demand coverage projection shared by presentation and exported reports; never changes calculations.</summary>
public static class EffectCoverage
{
    public const string ComparisonNote = "Comparisons use fixed assumptions, not measured combat uptime. A zero change can mean unmet conditions, utility, or missing mechanics. Listed channels do not establish complete talent support.";
    public const string NoModel = "No modeled effect found in pack data; a zero change does not evaluate this talent's described mechanics.";

    public static string ComparisonDisclosure(IEnumerable<EffectCoverageReport> reports)
    {
        var talents = reports.SelectMany(report => report.Talents).ToArray();
        // Static comparisons use every condition as chosen, including ones a talent-trigger simulation would drive.
        var assumed = talents.Where(talent => talent.Channels.Any(channel => channel.Kind is AssumptionConditions or DrivenConditions))
            .Select(talent => talent.Name).Distinct(StringComparer.Ordinal).ToArray();
        return ComparisonNote + Omissions(talents)
            + (assumed.Length == 0 ? "" : " Depends on chosen assumptions, a scenario rather than measured uptime (talent-trigger simulations drive some): " + string.Join(", ", assumed) + ".");
    }

    /// <summary>Talents with no modeled channel (unless the source audit calls them utility) and those the audit marks as
    /// leaving something out.</summary>
    internal static string Omissions(IEnumerable<TalentEffectCoverage> talents)
    {
        var rows = talents.ToArray();
        string Names(Func<TalentEffectCoverage, bool> where) => string.Join(", ", rows.Where(where).Select(talent => talent.Name).Distinct(StringComparer.Ordinal));
        var missing = Names(talent => !talent.Modeled && talent.AuditStatus != "utility");
        var parts = new (string Label, string Status)[] { ("partial", "partial"), ("not modeled", "unmodeled"), ("unknown", "unknown") }
            .Select(part => (part.Label, Names: Names(talent => talent.AuditStatus == part.Status))).Where(part => part.Names.Length > 0)
            .Select(part => $"{part.Label}: {part.Names}").ToArray();
        return (missing.Length == 0 ? "" : " No modeled pack channel: " + missing + ".")
            + (parts.Length == 0 ? "" : " Source audit, " + string.Join("; ", parts) + ".");
    }

    /// <summary>A talent's own damage over time (pack <c>simulation.talentDamage</c>, Toxic Delivery).</summary>
    public const string TalentDamage = "talent-damage";

    /// <summary>The producer's source audit of the talent's mechanic (pack <c>coverage</c>, R35).</summary>
    public const string SourceAudit = "source-audit";

    /// <summary>Channel kinds for a talent's stat modifiers, by what decides whether they apply.</summary>
    public const string StaticModifiers = "static-modifiers";
    public const string BuildConditionedModifiers = "build-conditioned-modifiers";
    public const string DrivenConditions = "driven-conditions";
    public const string AssumptionConditions = "assumption-conditions";
    public const string ExpectedValueModifiers = "expected-value-modifiers";

    public static EffectCoverageReport Describe(GamePack pack, BuildSnapshot build, ImmutableDictionary<string, string> context)
    {
        var loadout = LoadoutResolver.Resolve(pack, build);
        var assumptions = ResolveAssumptions.Resolve(pack, context);
        var inputs = assumptions.Explicit.SetItems(assumptions.Numeric.Select(pair =>
            KeyValuePair.Create(pair.Key, pair.Value.ToString("R", CultureInfo.InvariantCulture))));
        var talents = PackIndex.Of(pack).Talents;
        var copies = TalentCopySelection.Sources(pack, build, loadout, context);
        var rows = ImmutableArray.CreateBuilder<TalentEffectCoverage>();
        foreach (var entry in loadout.Equipped)
        {
            foreach (var slot in entry.Item.TalentSlots)
            {
                var id = build.TalentSelections.GetValueOrDefault(entry.SlotId)?.GetValueOrDefault(slot.ID) ?? "";
                if (!slot.Choices.Contains(id) || !talents.TryGetValue(id, out var talent)) { continue; }
                rows.Add(DescribeTalent(pack, talent, entry.SlotId, entry.EffectsActive(context), copies, inputs));
            }
        }
        // Some set talents (e.g. Dead Man's Hand) are inherent and have no selectable item talent slot.
        var equipped = loadout.Equipped.Select(entry => entry.Item.ID).ToHashSet(StringComparer.Ordinal);
        var representedSets = pack.Sets.Where(set => set.Items.Any(equipped.Contains)).Select(set => set.ID).ToHashSet(StringComparer.Ordinal);
        var selected = rows.Select(row => row.Talent).ToHashSet(StringComparer.Ordinal);
        var unselected = pack.Talents.Where(talent => !selected.Contains(talent.ID) && talent.ModeledBySets.Any(representedSets.Contains)).ToArray();
        // A set talent with a fight rule is only live once its set's top bonus applies; the rest are set-bonus models with any piece.
        var fullSetTalents = unselected.Any(talent => SetBonusSelection.RunsAtTopBonus(pack, talent))
            ? SetBonusSelection.FullSetTalents(pack, SetBonusSelection.Resolve(pack, equipped, selected).ActiveSets).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var talent in unselected)
        { rows.Add(DescribeTalent(pack, talent, "", !SetBonusSelection.RunsAtTopBonus(pack, talent) || fullSetTalents.Contains(talent.ID), copies, inputs)); }
        return new(rows.ToImmutable(), inputs) { Party = PartyEffectSelection.Select(pack, build) };
    }

    private static TalentEffectCoverage DescribeTalent(GamePack pack, TalentDefinition talent, string slot, bool active,
        ImmutableArray<SelectedTalentCopy> copies, ImmutableDictionary<string, string> inputs)
    {
        var channels = ImmutableArray.CreateBuilder<EffectChannel>();
        if (talent.Coverage is { } audit) { channels.Add(new(SourceAudit, [audit.Status], AuditNote(audit))); }
        channels.AddRange(DescribeModifiers(pack, talent, inputs));
        if (!talent.ModeledBySets.IsEmpty)
        {
            channels.Add(new("set-bonuses", talent.ModeledBySets, SetBonusSelection.RunsAtTopBonus(pack, talent)
            ? "Set-bonus model; equipment thresholds and selected assumptions apply. A fight runs this talent's rules once the set's top bonus applies."
            : "Static set-bonus model; equipment thresholds and selected assumptions apply. Card, cooldown and event behavior is not established by a set link."));
        }
        if (talent.SetPieceCredit != 0)
        { channels.Add(new("set-piece-credit", [talent.ID], $"Adds {talent.SetPieceCredit} set-piece credit to other equipped sets; stats change only when a bonus threshold changes.")); }
        if (talent.CopiesTalent is { } rule)
        {
            var selected = copies.Where(copy => copy.CopierSlot == slot && copy.Copier.ID == talent.ID).ToArray();
            var names = selected.Length == 0 ? "No eligible source talent is selected in this configuration."
                : "Eligible source: " + string.Join(", ", selected.Select(copy => copy.Talent.Name)) + ".";
            channels.Add(new("talent-copy", [.. selected.Select(copy => copy.Talent.ID)],
                $"Copies ordinary stat effects from {pack.Slots.FirstOrDefault(definition => definition.ID == rule.From)?.Name ?? rule.From} to {pack.Slots.FirstOrDefault(definition => definition.ID == rule.To)?.Name ?? rule.To}, subject to weapon/category exclusions. {names} Copied combat-trigger ownership is not modeled."));
        }
        if (!talent.AllyEffects.IsEmpty)
        { channels.Add(new("party-projection", [.. talent.AllyEffects.Select(effect => effect.ID)], "Party projection uses giver snapshots for other recipients; automatic triggers, recipient cooldowns and self-effects are not established by this projection.")); }
        var triggers = pack.Simulation.Triggers.Where(trigger => trigger.Talent == talent.ID).Select(trigger => trigger.Key).Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (!triggers.IsEmpty)
        { channels.Add(new("authored-combat-rules", triggers, "Pack combat rules exist; simulation mode and handler restrictions still apply. Static comparisons do not run these events.")); }
        // A talent's own damage over time (Toxic Delivery) runs only in driven simulations.
        var damage = pack.Simulation.TalentDamage.Where(rule => rule.Talent == talent.ID).Select(rule => rule.Total).ToImmutableArray();
        if (!damage.IsEmpty)
        { channels.Add(new(TalentDamage, damage, "Pack talent damage over time exists; driven simulations tick it on the current target. Static comparisons don't include it.")); }
        return new(slot, talent.ID, talent.Name, active, channels.ToImmutable());
    }

    /// <summary>
    /// Splits a talent's stat modifiers by what decides whether they apply: nothing (static), the build (a when
    /// condition without assumption reads), keys the talent's own combat rules drive, or assumptions nothing simulates.
    /// Assumption reads are the modifier's required context and the context values its condition and formula read.
    /// </summary>
    private static IEnumerable<EffectChannel> DescribeModifiers(GamePack pack, TalentDefinition talent, ImmutableDictionary<string, string> inputs)
    {
        if (talent.Modifiers.IsEmpty) { yield break; }
        // A set talent's rule drives its key for the talents reading it (Veer Override reads Apex Engine's stacks).
        var rules = pack.Simulation.Triggers.Where(rule => rule.Talent == talent.ID || PackIndex.Of(pack).RuledSetTalents.Any(ruled => ruled.ID == rule.Talent)).ToArray();
        var driven = rules.SelectMany(rule => new[] { rule.Key, rule.Bank }).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);
        var unconditional = 0;
        var buildConditioned = 0;
        var expected = 0;
        foreach (var modifier in talent.Modifiers)
        {
            // The pack's practical_* stats are per-shot rates (hits, headshots, kills); formulas over them average events.
            if (Identifiers(modifier.When).Union(modifier.Value.Formula is { } source ? Identifiers(source) : []).Any(id => id.StartsWith("practical_", StringComparison.Ordinal)))
            { expected++; }
            var reads = Reads(modifier.When).Union(modifier.Value.Formula is { } formula ? Reads(formula) : []).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, value) in modifier.RequiresContext ?? ImmutableDictionary<string, string>.Empty) { required[key] = value; }
            read.UnionWith(reads);
            if ((modifier.RequiresContext?.Count ?? 0) == 0 && reads.Count == 0)
            {
                if (modifier.When.Length == 0) { unconditional++; } else { buildConditioned++; }
            }
        }
        if (unconditional > 0)
        { yield return new(StaticModifiers, [talent.ID], "Static stat modifiers apply whenever the talent is active."); }
        if (buildConditioned > 0)
        { yield return new(BuildConditionedModifiers, [talent.ID], "Some stat modifiers apply only while their build condition holds (equipped items, sets or tags)."); }
        if (expected > 0)
        { yield return new(ExpectedValueModifiers, [talent.ID], "Some stat modifiers average an event-driven effect over practical hit, headshot or kill rates: an expected value, not event timing."); }
        var keys = required.Keys.Union(read).Order(StringComparer.Ordinal).ToArray();
        // A count needs several enemies at once, so elsewhere its key stays the assumption.
        var counted = rules.Where(rule => rule.Kind == "count" && !rules.Any(other => other.Kind != "count" && other.Key == rule.Key)).Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
        var countedKeys = keys.Where(counted.Contains).ToImmutableArray();
        var drivenKeys = keys.Where(key => driven.Contains(key) && !counted.Contains(key)).ToImmutableArray();
        var assumedKeys = keys.Where(key => !driven.Contains(key)).ToImmutableArray();
        if (!drivenKeys.IsEmpty)
        {
            yield return new(DrivenConditions, drivenKeys,
                $"Conditional on {State(drivenKeys)}, which this talent's combat rules drive in talent-trigger simulations; static comparisons use the assumption as chosen, not proc uptime.");
        }
        if (!countedKeys.IsEmpty)
        {
            yield return new(DrivenConditions, countedKeys,
                $"Conditional on {State(countedKeys)}, which this talent's combat rules count in fights with several enemies at once; any other fight and static comparisons use the assumption as chosen.");
        }
        if (!assumedKeys.IsEmpty)
        {
            yield return new(AssumptionConditions, assumedKeys,
                $"Conditional on {State(assumedKeys)}, an assumption no combat rule of this talent drives; results hold only as far as that assumption is realistic.");
        }

        string State(IEnumerable<string> names) => string.Join(", ", names.Select(key =>
        {
            var name = pack.Selectors.FirstOrDefault(input => input.ID == key)?.Name ?? pack.NumericContextInputs.FirstOrDefault(input => input.ID == key)?.Name ?? key;
            var now = inputs.TryGetValue(key, out var value) ? value : "unset";
            return required.TryGetValue(key, out var needed) ? $"{name} (needs {needed}, now {now})" : $"{name} (now {now})";
        }));
    }

    private static string AuditNote(TalentCoverage audit)
    {
        var omits = audit.Omits.IsDefaultOrEmpty ? "" : " Leaves out: " + string.Join("; ", audit.Omits) + ".";
        var verdict = audit.Status switch
        {
            "complete" => "the pack models the whole PvE mechanic.",
            "partial" => "the pack models part of the mechanic.",
            "utility" => "a utility effect with nothing to model for damage, armor or skills.",
            "unmodeled" => "the pack does not model the mechanic.",
            _ => "the source can't settle how much the pack models."
        };
        var settle = audit.SettleBy.Length == 0 ? "" : $" Settled by: {audit.SettleBy}.";
        return $"Source audit: {verdict}{omits}{settle} Evidence: {audit.Evidence}.";
    }

    // Coverage runs for every compared candidate, so each distinct pack expression is parsed once.
    private static readonly ConcurrentDictionary<string, Expression?> Parsed = new(StringComparer.Ordinal);
    private const string UnreadableCondition = "an unreadable condition";

    private static Expression? Parse(string expression) => expression.Length == 0 ? null : Parsed.GetOrAdd(expression, static source =>
    {
        try { return ExpressionParser.Parse(source); }
        catch (ExpressionException) { return null; }
    });

    // Loaded packs are validated; an unparsable expression is still reported conservatively as an assumption read.
    private static ImmutableHashSet<string> Reads(string expression) =>
        expression.Length == 0 ? [] : Parse(expression)?.ContextReads ?? [UnreadableCondition];

    private static ImmutableHashSet<string> Identifiers(string expression) => Parse(expression)?.Identifiers ?? [];
}
