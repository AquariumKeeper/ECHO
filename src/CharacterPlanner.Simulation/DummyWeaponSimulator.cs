using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// C8's first slice: one resolved weapon firing into an unlimited unarmored dummy. It never
/// invents weapon damage formulas: all four shot channels and expected damage come from Core.
/// This is deliberately separate from the future encounter API and is not wired to the UI.
/// </summary>
public static class DummyWeaponSimulator
{
    public const string Contract = "d2p-dummy-weapon-v1/" + SimulationRandom.Contract;
    private const int TraceLimit = 100000;
    private const long WorkLimit = 50000000;
    // A swap within this many seconds of the last kill is a swap after a kill (Finisher).
    private const double SwapAfterKillWindow = 10;

    /// <summary>
    /// The talents whose combat rules a fight with these options runs: those on equipped items whose effects are active (the weapon
    /// in hand, and with talent triggers the rotation's second weapon), full-set talents and active specialization nodes. The
    /// Desktop asks this for the rules a held skill can wait for (ADR 0014).
    /// </summary>
    public static IReadOnlySet<string> RuleTalents(CompiledPackExpressions pack, BuildSnapshot build, ImmutableDictionary<string, string> context, DummyFightOptions options)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (options.DriveSimpleBuffs || options.IncludeIncoming)
        {
            CollectRuleTalents(pack, build, context, options, selected, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        }
        return selected;
    }

    private static void CollectRuleTalents(CompiledPackExpressions pack, BuildSnapshot build, ImmutableDictionary<string, string> context, DummyFightOptions options,
        HashSet<string> selected, HashSet<string> setTalents, HashSet<string> gearTalents, Dictionary<string, HashSet<string>> weaponTalents)
    {
        foreach (var item in LoadoutResolver.Resolve(pack.Pack, build).Equipped.Where(item => item.EffectsActive(context) ||
            options.DriveSimpleBuffs && item.WeaponSlot && item.SlotId == options.Rotation?.SecondSlot))
        {
            foreach (var slot in item.Item.TalentSlots)
            {
                var id = build.TalentSelections.GetValueOrDefault(item.SlotId)?.GetValueOrDefault(slot.ID, "") ?? "";
                if (slot.Choices.Contains(id))
                {
                    selected.Add(id);
                    if (!item.WeaponSlot) { gearTalents.Add(id); }
                    else
                    {
                        if (!weaponTalents.TryGetValue(item.SlotId, out var owned)) { weaponTalents[item.SlotId] = owned = new(StringComparer.Ordinal); }
                        owned.Add(id);
                    }
                }
            }
        }
        // ADR 0031: a selected buff hive owns the rules its charges fire, and hears them as gear does. Without skills its charges never
        // come, so its rules take no part and its key reads the user's setting, as when it steps aside without triggers.
        var nodes = pack.Pack.Specializations.SelectMany(specialization => specialization.Nodes).Select(node => node.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var skill in build.ActiveSkillSelections.Values.Where(id => options.IncludeSkills && pack.Pack.Simulation.Triggers.Any(rule => rule.Talent == id) &&
            pack.Pack.Talents.All(talent => talent.ID != id) && !nodes.Contains(id)))
        {
            selected.Add(skill);
            gearTalents.Add(skill);
        }
        // The resolver's own sets, so a talent's rule runs exactly when the row reading its key applies.
        if (SetBonusSelection.MayRunSetTalents(pack.Pack, LoadoutResolver.Resolve(pack.Pack, build).Equipped.Select(item => item.Item.ID)))
        {
            foreach (var id in SetBonusSelection.FullSetTalents(pack.Pack, BuildResolver.Resolve(pack, build, context).ActiveSets))
            {
                selected.Add(id);
                gearTalents.Add(id);
                setTalents.Add(id);
            }
        }
        // A specialization node's rule runs while the node is active, and it reacts as gear does (R56). Like a set talent's, it's
        // checked at setup for what a fight holds fixed.
        if (pack.Pack.Specializations.FirstOrDefault(specialization => specialization.ID == build.SpecializationId) is { } chosen)
        {
            foreach (var id in SpecializationRules.Status(chosen, build.SpecializationNodeSelections).Active)
            {
                selected.Add(id);
                gearTalents.Add(id);
                setTalents.Add(id);
            }
        }
    }

    // Fights track a skill's own burn and bleed only, so only those are copied to other enemies (ADR 0015).
    // A skill's burn, bleed or poison copies onto other enemies (ADR 0015, ADR 0024).
    private static bool SkillStatusCopies(string status) => GamePack.DamagingStatuses.Contains(status);

    // A pass-on with its withinStat and shareStat read; a value outside the field's own limits refuses the fight.
    private static SimPassOnDefinition PassOnFromStats(SimPassOnDefinition rule, IReadOnlyDictionary<string, double> stats)
    {
        if (rule.WithinStat.Length > 0)
        {
            if (!stats.TryGetValue(rule.WithinStat, out var within) || !double.IsFinite(within) || within <= 0 || within > SimPassOnDefinition.MaximumWithin)
            { throw new SimulationException("trigger-unsupported", $"withinStat {rule.WithinStat} needs a value above 0 up to {SimPassOnDefinition.MaximumWithin:G} metres"); }
            rule = rule with { Within = within, WithinStat = "" };
        }
        if (rule.ShareStat.Length > 0)
        {
            if (!stats.TryGetValue(rule.ShareStat, out var share) || !double.IsFinite(share) || share <= 0 || share > 1)
            { throw new SimulationException("trigger-unsupported", $"shareStat {rule.ShareStat} needs a value above 0 up to 1"); }
            rule = rule with { Share = share, ShareStat = "" };
        }
        return rule;
    }

    public static DummyFightResult Run(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> assumptions, DummyFightOptions? options = null,
        CancellationToken cancellationToken = default, IProgress<SimulationProgress>? progress = null)
    {
        return RunCore(pack, build, assumptions, options, cancellationToken, progress, false, 0);
    }

    /// <summary>Target fights with explicit options for weapon rotation, skill damage, triggers and incoming damage.</summary>
    public static DummyFightResult RunAgainstTargets(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> assumptions, DummyFightOptions? options = null, double retargetDelay = 1,
        CancellationToken cancellationToken = default, IProgress<SimulationProgress>? progress = null) =>
        RunCore(pack, build, assumptions, options, cancellationToken, progress, true, retargetDelay);

    private static DummyFightResult RunCore(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> assumptions, DummyFightOptions? options,
        CancellationToken cancellationToken, IProgress<SimulationProgress>? progress, bool targeted, double retargetDelay)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(assumptions);
        // A fight reads an unset selector at its default, as a calculation does; the fight's own readings set theirs over it.
        assumptions = ResolveAssumptions.WithSelectorDefaults(pack.Pack, assumptions);
        options ??= new();
        options.Validate();
        // ADR 0023: a fight whose rules play talents' mechanics sets their static values aside; one without keeps them, so a static value
        // with no rule to play it still refuses where it would be wrong. The user's own setting never reaches it.
        assumptions = assumptions.SetItem(GamePack.FightContextSelector, options.DriveSimpleBuffs ? GamePack.FightContextFight : GamePack.FightContextStatic);
        cancellationToken.ThrowIfCancellationRequested();
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        if (LoadoutResolver.EquipmentLimitProblem(pack.Pack, build) is { } equipmentProblem)
        { throw new SimulationException(equipmentProblem.Code, equipmentProblem.Message); }
        if (!double.IsFinite(retargetDelay) || retargetDelay < 0 || retargetDelay > 60) { throw new ArgumentOutOfRangeException(nameof(retargetDelay)); }
        if (options.Distance is not null && !options.DriveSimpleBuffs)
        { throw new SimulationException("distance-unsupported", "Fight distance only applies when trigger-driven buffs are enabled"); }
        if (options.ClearKills is not null && !targeted)
        { throw new SimulationException("target-required", "Clearing a number of targets requires targets"); }
        var sequence = options.TargetSequence.IsDefault ? [] : options.TargetSequence;
        var distances = options.TargetDistances.IsDefault ? [] : options.TargetDistances;
        if (!distances.IsEmpty && !targeted) { throw new SimulationException("target-required", "Target distances require targets"); }
        if (!distances.IsEmpty && DummyFightOptions.TargetDistancesProblem(options.DriveSimpleBuffs) is { } distancesProblem)
        { throw new SimulationException("target-distances-unsupported", distancesProblem); }
        // The distance a fight starts at: the first target's, or the fight's own without target distances.
        var firstDistance = distances.IsEmpty ? options.Distance : distances[0];
        // How many of these places in the fight's order are within a range, 0 for no limit, each at its own distance.
        int CountPresent(IEnumerable<int> places, double within) => places.Count(place =>
            within == 0 || (distances.IsEmpty ? options.Distance!.Value : distances[place % distances.Length]) <= within);
        if (!sequence.IsEmpty)
        {
            if (!targeted) { throw new SimulationException("target-required", "A target sequence requires targets"); }
            // Each target's profile joins the driven state, so the sequence needs driven fights.
            if (DummyFightOptions.TargetSequenceProblem(options.DriveSimpleBuffs) is { } problem)
            { throw new SimulationException("target-sequence-unsupported", problem); }
        }
        if (options.IncludeShield && !options.IncludeIncoming)
        { throw new SimulationException("shield-mode-unsupported", "Shield simulation requires incoming fire"); }
        if (options.IncludeIncoming && (!targeted || options.Mode != SimulationMode.Random))
        { throw new SimulationException("incoming-mode-unsupported", "Incoming fire requires random target fights"); }
        // Swaps under fire need the driven hand, whose recovery re-resolves with each weapon.
        if (options.IncludeIncoming && options.Rotation is not null && !options.DriveSimpleBuffs)
        { throw new SimulationException("incoming-rotation-unsupported", "Weapon swaps under incoming fire require talent triggers"); }
        var cycle = options.HasCoverCycle ? new CoverCycle(options.PeekSeconds!.Value, options.CoverSeconds!.Value) : null;
        if (cycle is not null && DummyFightOptions.CoverCycleProblem(options.DriveSimpleBuffs) is { } coverProblem)
        { throw new SimulationException("cover-cycle-unsupported", coverProblem); }
        var context = targeted ? assumptions : assumptions.SetItem("enemy_target", "0");
        // The player shoots from cover, so in-cover effects hold for the whole fight (ADR 0009).
        var coverSelector = cycle is not null && pack.Pack.Selectors.Any(selector => selector.ID == DummyFightOptions.CoverInput);
        if (coverSelector) { context = context.SetItem(DummyFightOptions.CoverInput, "true"); }
        if (options.IncludeIncoming && pack.Pack.NumericContextInputs.Any(input => input.ID == "makeshift_qualifying_damage"))
        { context = context.SetItem("makeshift_qualifying_damage", "0"); }
        // An input the user left unset takes the pack's default for fights with enemy fire (owner, 2026-11-14), such as a gentler target
        // than the general default; one they set keeps their value. Both are noted. A target sequence sets each target's profile itself.
        var incomingNotes = new List<string>();
        var incomingInputs = new List<string>();
        if (options.IncludeIncoming && !pack.Pack.Simulation.IncomingDefaults.IsDefaultOrEmpty)
        {
            foreach (var entry in pack.Pack.Simulation.IncomingDefaults)
            {
                if (pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == entry.Input) is not { } input || !input.Accepts(entry.Value))
                { throw new SimulationException("incoming-default-invalid", $"The default for {entry.Input} in fights with enemy fire has no legal input"); }
                var shown = input.Shown(entry.Value);
                if (entry.Input == DummyFightOptions.TargetProfileInput && !sequence.IsEmpty)
                { incomingNotes.Add($"{input.Name}'s default in fights with enemy fire ({shown}) is not used: the target sequence sets each target's profile"); continue; }
                if (context.TryGetValue(entry.Input, out var set))
                {
                    var chosen = double.TryParse(set, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
                    incomingNotes.Add(chosen == entry.Value ? $"{input.Name} is set to {shown}, the same as its default in fights with enemy fire" :
                        $"{input.Name} is set to {(double.IsNaN(chosen) ? set : input.Shown(chosen))}; its default in fights with enemy fire ({shown}) is not used");
                    continue;
                }
                context = context.SetItem(entry.Input, entry.Value.ToString("0.##########", CultureInfo.InvariantCulture));
                incomingInputs.Add(input.Name);
                incomingNotes.Add($"{input.Name} takes its default for fights with enemy fire: {shown}");
            }
        }
        // An assumption the user left unset takes its default for the fight distance (P12: enemy hit rate by distance); one
        // they set keeps their value. Both are noted in the result. The validator keeps driven keys out of these. They describe
        // enemy fire, so a fight without it neither takes nor reports them.
        var distanceNotes = new List<string>();
        var distanceInputs = new List<string>();
        // With target distances each target has its own band: these inputs ride on the driven state, with their values by distance.
        var bandInputs = new List<string>();
        var distanceBands = new Dictionary<double, List<(string Input, string Value)>>();
        if (options.IncludeIncoming && firstDistance is { } defaultsDistance && !pack.Pack.Simulation.DistanceDefaults.IsDefaultOrEmpty)
        {
            var inputs = pack.Pack.NumericContextInputs.GroupBy(input => input.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            static string Metres(double meters) => meters.ToString("0.##", CultureInfo.InvariantCulture) + " m";
            double[] readings = distances.IsEmpty ? [defaultsDistance] : [.. distances.Distinct()];
            foreach (var entry in pack.Pack.Simulation.DistanceDefaults)
            {
                if (!inputs.TryGetValue(entry.Input, out var input))
                { throw new SimulationException("distance-default-invalid", $"The distance default for {entry.Input} has no input"); }
                var isSet = context.TryGetValue(entry.Input, out var set);
                // A value set for every target needs no bands at all.
                if (isSet && !distances.IsEmpty) { distanceNotes.Add($"{input.Name} is set to {set}; its defaults by distance are not used"); continue; }
                var bands = new List<(double Meters, string Value, double? UpTo)>();
                foreach (var meters in readings)
                {
                    if (entry.Bands.FirstOrDefault(band => band.UpTo is not { } upTo || meters <= upTo) is not { } band || !input.Accepts(band.Value))
                    { throw new SimulationException("distance-default-invalid", $"The distance default for {entry.Input} has no legal band at {Metres(meters)}"); }
                    bands.Add((meters, band.Value.ToString("0.##########", CultureInfo.InvariantCulture), band.UpTo));
                }
                if (isSet) { distanceNotes.Add($"{input.Name} is set to {set}; its default at {Metres(bands[0].Meters)} ({bands[0].Value}) is not used"); continue; }
                context = context.SetItem(entry.Input, bands[0].Value);
                distanceInputs.Add(input.Name);
                if (distances.IsEmpty)
                {
                    distanceNotes.Add($"{input.Name} follows the fight distance: {bands[0].Value} at {Metres(bands[0].Meters)}" +
                        (bands[0].UpTo is { } within ? $" (up to {within.ToString("0.##", CultureInfo.InvariantCulture)} m)" : " (beyond the pack's bands)"));
                    continue;
                }
                bandInputs.Add(entry.Input);
                foreach (var (meters, value, _) in bands)
                {
                    if (!distanceBands.TryGetValue(meters, out var values)) { distanceBands[meters] = values = []; }
                    values.Add((entry.Input, value));
                }
                distanceNotes.Add($"{input.Name} follows each target's distance: {string.Join(", ", bands.Select(band => $"{band.Value} at {Metres(band.Meters)}"))}");
            }
        }
        ImmutableDictionary<string, string> AtDistance(ImmutableDictionary<string, string> read, double meters)
        {
            foreach (var (input, value) in distanceBands[meters]) { read = read.SetItem(input, value); }
            return read;
        }
        var buffRules = ImmutableArray<SimTriggerDefinition>.Empty;
        var magazineRules = ImmutableArray<SimTriggerDefinition>.Empty;
        // The buff rules a fight runs: the selected rules, with each copy of a talent on both rotated weapons split out.
        var fightBuffRules = ImmutableArray<SimTriggerDefinition>.Empty;
        // For each driven key of such a talent, the internal key of each weapon's copy; the copy in hand supplies the key.
        var copiedKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // The talents on both rotated weapons whose shared rules (shareCopies, ADR 0019) drive one key, for the result's note.
        var sharedCopyNotes = new List<string>();
        PhaseTriggerState? phases = null;
        CombatTriggerState? combat = null;
        EnemyCountState? enemyCounts = null;
        var uncountedKeys = ImmutableArray<string>.Empty;
        var kitlessTalents = ImmutableArray<string>.Empty;
        var swaplessTalents = ImmutableArray<string>.Empty;
        var distancelessTalents = ImmutableArray<string>.Empty;
        // Bonus armor readings (ADR 0021), and the talents whose reading keeps the user's setting in a fight without enemy fire.
        var readsBonusArmor = false;
        var unfiredBonusTalents = ImmutableArray<string>.Empty;
        var skilllessTalents = ImmutableArray<string>.Empty;
        var shieldlessTalents = ImmutableArray<string>.Empty;
        var skillEventTalents = ImmutableArray<string>.Empty;
        var statusRules = ImmutableArray<SimTriggerDefinition>.Empty;
        var dotRules = ImmutableArray<SimStatusDamageDefinition>.Empty;
        var talentDamageRules = ImmutableArray<SimTalentDamageDefinition>.Empty;
        var blastRules = ImmutableArray<SimBlastDefinition>.Empty;
        var spreadRules = ImmutableArray<SimSpreadDefinition>.Empty;
        var redirectRules = ImmutableArray<SimRedirectDefinition>.Empty;
        var trapRules = ImmutableArray<SimTrapDefinition>.Empty;
        var passOnRules = ImmutableArray<SimPassOnDefinition>.Empty;
        var drivenKeys = ImmutableArray<string>.Empty;
        // ADR 0036: the zones a selected skill marks, unless its gate makes it step aside, and the key its mark sets in a fight driving keys.
        var zones = !options.IncludeSkills ? null : pack.Pack.Simulation.Skills
            .FirstOrDefault(rule => rule.Zones is not null && build.ActiveSkillSelections.Values.Contains(rule.Skill) && ResolveAssumptions.Matches(rule.RequiresContext, context))?.Zones;
        var zoneKey = options.DriveSimpleBuffs && zones is { Key.Length: > 0 } ? zones.Key : "";
        // A talent's consecutive-headshot cycle (ADR 0021: Headache), in driven fights; its key is driven like a trigger's.
        SimHeadshotCycle? cycleRow = null;
        // A talent's bonus on the hit after a headshot kill (ADR 0023: Headhunter), in driven fights.
        SimNextHitBonus? bonusRow = null;
        // Keys of rules a fight's context gates off, each held off for the fight: a count at 0, a flag unset. They're constant, so they
        // aren't driven keys, and a skill held on one is refused as one on any key no rule drives.
        var gatedOff = ImmutableArray<(string Key, bool Counted)>.Empty;
        var pickupsUsed = false;
        var secondsUsed = false;
        var reloadEndUsed = false;
        var hitPelletUsed = false;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        // Set talents a fight runs at their set's top bonus, and active specialization nodes: owners checked at setup.
        var setTalents = new HashSet<string>(StringComparer.Ordinal);
        var gearTalents = new HashSet<string>(StringComparer.Ordinal);
        // Under rotation both weapons' talents take part; each reacts only while its weapon is in hand.
        var weaponTalents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (options.DriveSimpleBuffs || options.IncludeIncoming)
        {
            CollectRuleTalents(pack, build, context, options, selected, setTalents, gearTalents, weaponTalents);
        }
        if (options.DriveSimpleBuffs)
        {
            if (options.Mode != SimulationMode.Random || options.IncludeSkills && !options.IncludeIncoming)
            { throw new SimulationException("buff-mode-unsupported", "Driven combat requires random fights; damaging skills also require incoming mode"); }
            // A copied talent's stat effects resolve in Core, for each weapon in hand; only a copied talent with its own combat
            // rules would need copied trigger ownership (Abridged copying a talent such as Strained).
            if (pack.Pack.Talents.Any(talent => selected.Contains(talent.ID) && talent.CopiesTalent is not null))
            {
                var loadout = LoadoutResolver.Resolve(pack.Pack, build);
                var hands = new[] { context.GetValueOrDefault(GamePack.WeaponSlotSelector), options.Rotation?.SecondSlot }.OfType<string>();
                var copiedTalents = hands.SelectMany(hand => TalentCopySelection.Sources(pack.Pack, build, loadout, context.SetItem(GamePack.WeaponSlotSelector, hand)))
                    .Select(copy => copy.Talent.ID).ToHashSet(StringComparer.Ordinal);
                if (copiedTalents.Any(id => pack.Pack.Simulation.Triggers.Any(rule => rule.Talent == id) || pack.Pack.Simulation.TalentDamage.Any(rule => rule.Talent == id)))
                { throw new SimulationException("buff-copy-unsupported", "A copied talent with its own combat rules needs copied trigger ownership, which is not implemented"); }
            }
            // Each state class owns its rule kinds; everything else is event-driven buff state.
            var groups = pack.Pack.Simulation.Triggers.Where(rule => selected.Contains(rule.Talent)).ToLookup(rule =>
                PhaseTriggerState.Kinds.Contains(rule.Kind) ? nameof(PhaseTriggerState) :
                CombatTriggerState.Kinds.Contains(rule.Kind) ? nameof(CombatTriggerState) :
                rule.Kind == EnemyCountState.Kind ? nameof(EnemyCountState) :
                rule.Kind == StatusTriggerState.Kind ? nameof(StatusTriggerState) : nameof(TimedBuffState));
            var phaseRules = groups[nameof(PhaseTriggerState)].ToArray();
            var combatRules = groups[nameof(CombatTriggerState)].ToArray();
            // Without a fight distance, a distance rule that may assume (Versatile's bands, ADR 0019) steps aside in the combat state, which
            // still checks it: its key reads the user's setting, and the result names the talent. Any other distance rule needs a distance.
            if (firstDistance is null)
            {
                distancelessTalents = [.. combatRules.Where(rule => rule.AssumeWithoutDistance).Select(rule => rule.Talent).Distinct(StringComparer.Ordinal)
                    .Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
            }
            readsBonusArmor = options.IncludeIncoming && combatRules.Any(rule => rule.Kind == "bonus_armor");
            if (!options.IncludeIncoming)
            {
                unfiredBonusTalents = [.. combatRules.Where(rule => rule.Kind == "bonus_armor").Select(rule => rule.Talent).Distinct(StringComparer.Ordinal)
                    .Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
            }
            // Only a fight with several enemies knows who's around. One at a time, the talents read the user's assumption.
            var countRules = options.EnemiesAtOnce is null ? [] : groups[nameof(EnemyCountState)].ToArray();
            // A count on a status replaces the reading of the enemy shot (Bleeding Edge), which still runs with one enemy at a time.
            statusRules = [.. groups[nameof(StatusTriggerState)].Where(rule => !countRules.Any(count => count.Key == rule.Key))];
            uncountedKeys = options.EnemiesAtOnce is null ? [.. groups[nameof(EnemyCountState)].Select(rule => rule.Key)
                .Where(key => !statusRules.Any(rule => rule.Key == key)).Distinct().Order(StringComparer.Ordinal)] : [];
            // A rule that requires selector values the fight's context doesn't hold (Calculated outside cover) takes no part, and its key
            // stays off for the fight rather than taking the user's setting: the fight has decided the talent can't act.
            var fightContext = context;
            bool Meets(SimTriggerDefinition rule) => ResolveAssumptions.Matches(rule.RequiresContext, fightContext);
            var meets = groups[nameof(TimedBuffState)].ToLookup(Meets);
            buffRules = [.. meets[true]];
            // A primed rule (ADR 0019) is primed by its weapon's time away; a gear talent is never away, so it would never prime, with
            // a rotation or without.
            if (buffRules.Any(rule => rule.PrimeHolstered > 0 && gearTalents.Contains(rule.Talent)))
            { throw new SimulationException("trigger-unsupported", "A primed rule needs a weapon talent: gear is never put away"); }
            // A buff or status rule whose every gain is an event this fight never has steps aside, so its talent reads the user's
            // assumption, as counts do one enemy at a time; the talent is named, unless another rule still drives its key. A talent on
            // gear is no reason to refuse the fight. Talent damage on such an event never fires, and its talent is named too.
            ImmutableArray<string> StepAside(Func<string, bool> never, bool swapless)
            {
                bool Aside(SimTriggerDefinition rule)
                {
                    // Without a rotation a primed rule never primes, and a gate on differing weapon types has no second weapon to read.
                    if (swapless && (rule.PrimeHolstered > 0 || rule.DifferentTypes)) { return true; }
                    var gains = (rule.On.Length > 0 ? [rule.On] : Array.Empty<string>()).Concat(rule.Also.Select(gain => gain.On)).ToArray();
                    return gains.Length > 0 && gains.All(never);
                }
                var aside = buffRules.Concat(statusRules).Where(Aside).ToArray();
                buffRules = [.. buffRules.Where(rule => !Aside(rule))];
                statusRules = [.. statusRules.Where(rule => !Aside(rule))];
                var driven = buffRules.Concat(statusRules).Concat(phaseRules).Concat(combatRules).Concat(countRules)
                    .Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
                return [.. aside.Where(rule => !driven.Contains(rule.Key)).Select(rule => rule.Talent)
                    .Concat(pack.Pack.Simulation.TalentDamage.Where(rule => selected.Contains(rule.Talent) && never(rule.On)).Select(rule => rule.Talent))
                    .Distinct(StringComparer.Ordinal).Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
            }
            // With no kits, a rule that gains only on one steps aside.
            if (options.ArmorKits is null) { kitlessTalents = StepAside(name => name is "armor_kit" or "kit_ability", swapless: false); }
            // Without a rotation nothing swaps (ADR 0019): a rule that gains only on swaps, is primed by its weapon's time away, or gates on
            // differing weapon types steps aside. A swap among a rule's other gains simply never fires.
            static bool OnSwap(string name) => name is "swap" or "swapped" or "swap_after_kill";
            if (options.Rotation is null) { swaplessTalents = StepAside(OnSwap, swapless: true); }
            // A key another rule of the build drives, of any kind, stays with that rule. A gated rule's bank is held at 0 with its key, and a
            // key gated off as both a count and a flag reads 0.
            var keptKeys = buffRules.Concat(statusRules).Concat(phaseRules).Concat(combatRules).Concat(countRules)
                .SelectMany(rule => new[] { rule.Key, rule.Bank }).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
            gatedOff = [.. meets[false].SelectMany(rule => new[] { (rule.Key, Counted: rule.Kind == "stacks") }
                    .Concat(rule.Bank.Length > 0 ? [(rule.Bank, true)] : []))
                .Where(off => !keptKeys.Contains(off.Key))
                .GroupBy(off => off.Key, StringComparer.Ordinal).Select(keys => (keys.Key, keys.Any(off => off.Counted)))
                .OrderBy(off => off.Key, StringComparer.Ordinal)];
            // With no skills, a skill's hit or kill never comes. Its rules stay, so their keys stay driven and off unless another event gains
            // them (Outsider's and Preservation's weapon kills), and never take the user's setting; the result names their talents.
            static bool OnSkill(string name) => name is "skill_hit" or "skill_kill";
            skillEventTalents = [.. buffRules.Concat(statusRules).Concat(countRules)
                .Where(rule => OnSkill(rule.On) || rule.Also.Any(gain => OnSkill(gain.On)) || rule.OtherEvents.Any(OnSkill))
                .Select(rule => rule.Talent)
                .Concat(pack.Pack.Simulation.TalentDamage.Where(rule => selected.Contains(rule.Talent) && OnSkill(rule.On)).Select(rule => rule.Talent))
                .Distinct(StringComparer.Ordinal).Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
            if (!options.IncludeSkills) { skilllessTalents = skillEventTalents; }
            if (!options.IncludeShield)
            {
                shieldlessTalents = [.. buffRules.Concat(statusRules).Concat(countRules)
                    .Where(rule => rule.On == "shield_break" || rule.Also.Any(gain => gain.On == "shield_break") || rule.OtherEvents.Contains("shield_break"))
                    .Select(rule => rule.Talent).Distinct(StringComparer.Ordinal).Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
            }
            if (phaseRules.Concat(combatRules).Concat(countRules).Concat(statusRules).Concat(buffRules).Any(rule =>
                options.IncludeIncoming && rule.Key == "makeshift_qualifying_damage"))
            { throw new SimulationException("trigger-key-conflict", "Recovery inputs have a dedicated owner"); }
            magazineRules = [.. phaseRules.Where(rule => rule.Kind == "magazine")];
            var readings = phaseRules.Concat(combatRules).Concat(countRules).ToArray();
            if (readings.Select(rule => rule.Key).Distinct().Count() != readings.Length || readings.Any(reading => buffRules.Concat(statusRules).Any(rule => rule.Key == reading.Key)) ||
                statusRules.Any(status => buffRules.Any(rule => rule.Key == status.Key)))
            { throw new SimulationException("trigger-key-conflict", "Phase keys cannot share event-driven state"); }
            phases = new PhaseTriggerState(phaseRules);
            phases.ValidatePack(pack);
            context = phases.Context(context, 0, 1, 1);
            combat = new CombatTriggerState(pack, combatRules, distanceKnown: firstDistance is not null);
            if (combat.NeedsDistance && firstDistance is null) { throw new SimulationException("distance-required", "Selected distance rules require an explicit fight distance"); }
            context = combat.Context(context, firstDistance ?? 0, 0, 100);
            if (countRules.Length > 0)
            {
                enemyCounts = new EnemyCountState(pack, countRules);
                if (enemyCounts.NeedsDistance && firstDistance is null) { throw new SimulationException("distance-required", "Selected enemy counts require an explicit fight distance"); }
                context = enemyCounts.Context(context, (rule, _) => CountPresent(Enumerable.Range(0, options.EnemiesAtOnce!.Value), rule.Within));
            }
            var supportedEvents = new[] { "shot", "hit", "hit_pellet", "crit", "crit_pellet", "headshot", "body_hit", "unarmored_hit", "kill", "crit_kill", "headshot_kill", "reload", "reload_empty", "reload_end", "status_applied", "armor_kit", "kit_ability", "pickup", "second" };
            if (options.FocusSeconds is not null) { supportedEvents = [.. supportedEvents, "target_changed"]; }
            // Without skills they're never emitted, so their rules simply never fire (named above).
            supportedEvents = [.. supportedEvents, "skill_hit", "skill_kill", "skill_charge", "skill_out"];
            if (options.IncludeIncoming) { supportedEvents = [.. supportedEvents, "damaged", "armor_break"]; }
            // A fight without a shield, from the build or by the fight's choice, has none to break, so a rule on its breaking (Restrained)
            // simply never fires, as skill events don't in a fight without skills; the result names their talents.
            supportedEvents = [.. supportedEvents, "shield_break"];
            if (options.Rotation is not null) { supportedEvents = [.. supportedEvents, "swap", "swapped", "swap_after_kill"]; }
            // A skill's kill is already a kill to gear talents, so a gear talent's count naming both would count it twice.
            if (statusRules.Any(rule => gearTalents.Contains(rule.Talent) && new[] { rule.On }.Concat(rule.Also.Select(gain => gain.On)).ToHashSet(StringComparer.Ordinal).IsSupersetOf(["kill", "skill_kill"])))
            { throw new SimulationException("trigger-unsupported", "A gear talent's kill already counts a skill's kill, so its count can't name skill_kill as well"); }
            // Without a rotation a swap among a status rule's gains never fires; a count still needs its events.
            bool Heard(string name, SimTriggerDefinition rule) => supportedEvents.Contains(name) || options.Rotation is null && OnSwap(name) && rule.Kind != EnemyCountState.Kind;
            if (statusRules.Concat(countRules).Any(rule => rule.On.Length > 0 && !Heard(rule.On, rule) || rule.Also.Any(gain => !Heard(gain.On, rule))))
            { throw new SimulationException("trigger-event-unsupported", "A status or count rule requires an unimplemented event"); }
            // A single weapon never swaps, so a swap event that only ends, resets or consumes a rule (Frenzy's removal on weapon
            // switch) simply never fires, as does one among a rule's gains; a rule gaining only on swaps has stepped aside above.
            string[] neverFires = [.. options.Rotation is null ? ["swap", "swapped", "swap_after_kill"] : Array.Empty<string>(),
                .. options.FocusSeconds is null ? ["target_changed"] : Array.Empty<string>()];
            // A holstered-gain rule (Quick Draw) may gain on time alone, with no event. Only buff-type rules hear target_death.
            string[] buffEvents = [.. supportedEvents, "target_death"];
            bool Clears(string name) => name.Length == 0 || buffEvents.Contains(name) || neverFires.Contains(name);
            // Without a rotation a rule's swap gain never fires; with one, swaps are among the fight's events.
            bool Gains(string name) => buffEvents.Contains(name) || options.Rotation is null && OnSwap(name);
            if (buffRules.Any(rule => rule.On.Length > 0 && !Gains(rule.On) ||
                rule.Also.Any(gain => !Gains(gain.On)) ||
                rule.OtherEvents.Any(name => !Clears(name))))
            { throw new SimulationException("trigger-event-unsupported", "A selected rule requires an unimplemented event"); }
            context = new StatusTriggerState(pack, statusRules).Context(context, 0);
            // A selected talent's blast on other enemies (ADR 0013).
            blastRules = [.. pack.Pack.Simulation.Blasts.Where(blast => selected.Contains(blast.Talent))];
            spreadRules = [.. pack.Pack.Simulation.Spreads.Where(spread => selected.Contains(spread.Talent))];
            redirectRules = [.. pack.Pack.Simulation.Redirects.Where(redirect => selected.Contains(redirect.Talent))];
            trapRules = [.. pack.Pack.Simulation.Traps.Where(trap => selected.Contains(trap.Talent))];
            // One share of a hit moves; two redirects on a build would need an order the source doesn't give.
            if (redirectRules.Length > 1) { throw new SimulationException("trigger-unsupported", "A build can have one talent that redirects weapon damage"); }
            passOnRules = [.. pack.Pack.Simulation.PassOns.Where(passOn => selected.Contains(passOn.Talent))];
            if (blastRules.Any(rule => !supportedEvents.Contains(rule.On)))
            { throw new SimulationException("trigger-event-unsupported", "A blast requires an unimplemented event"); }
            // Only statuses a selected talent applies tick, by a rule or a blast; observer rules never apply one.
            // A copy of a skill's own burn or bleed ticks as the player's (ADR 0015), so it needs that status's ticks even with no talent
            // applying it.
            bool CopiesSkillStatus(string status) => options.IncludeSkills && SkillStatusCopies(status) &&
                (spreadRules.Any(rule => rule.SkillStatuses) || passOnRules.Any(rule => rule.SkillStatuses && (rule.Status == "any" || rule.Status == status)) ||
                    // A skill's area puts its status on other enemies as a status (ADR 0016), in a fight where it can reach them.
                    options.EnemiesAtOnce is not null && options.EnemySpacing is not null &&
                    pack.Pack.Simulation.Skills.Any(rule => rule.Area.Length > 0 && rule.Status == status && build.ActiveSkillSelections.Values.Contains(rule.Skill) &&
                        ResolveAssumptions.Matches(rule.RequiresContext, fightContext)));
            dotRules = [.. pack.Pack.Simulation.StatusDamage.Where(damage => statusRules.Any(rule => rule.On.Length > 0 && rule.Status == damage.Status) ||
                blastRules.Any(rule => rule.Status == damage.Status) || trapRules.Any(rule => rule.Status == damage.Status) || CopiesSkillStatus(damage.Status))];
            // A selected talent's own damage over time (Toxic Delivery) ticks beside the statuses.
            talentDamageRules = [.. pack.Pack.Simulation.TalentDamage.Where(damage => selected.Contains(damage.Talent))];
            if (talentDamageRules.Any(rule => !supportedEvents.Contains(rule.On) && !(options.Rotation is null && OnSwap(rule.On))))
            { throw new SimulationException("trigger-event-unsupported", "A talent damage rule requires an unimplemented event"); }
            var setupBuffs = new TimedBuffState(buffRules);
            setupBuffs.ValidatePack(pack);
            context = setupBuffs.Context(context, 0);
            foreach (var (key, counted) in gatedOff) { context = counted ? context.SetItem(key, "0") : context.Remove(key); }
            bool Uses(string name) => buffRules.Concat(statusRules).Concat(countRules).Any(rule => rule.On == name || rule.Also.Any(gain => gain.On == name) ||
                rule.OtherEvents.Contains(name));
            pickupsUsed = Uses("pickup");
            secondsUsed = Uses("second");
            reloadEndUsed = Uses("reload_end");
            hitPelletUsed = Uses("hit_pellet") || talentDamageRules.Any(rule => rule.On == "hit_pellet");
            // Bank keys are driven too: a pool cap changes them, and resolved states are cached by every driven key.
            var cycleRows = pack.Pack.Simulation.HeadshotCycles.Where(row => selected.Contains(row.Talent)).ToArray();
            if (cycleRows.Length > 1)
            {
                throw new SimulationException("headshot-cycle-unsupported",
                    $"{string.Join(" and ", cycleRows.Select(row => RuleOwnerName(pack.Pack, row.Talent)))} each count headshots, which one build can't have together");
            }
            cycleRow = cycleRows.FirstOrDefault();
            var bonusRows = pack.Pack.Simulation.NextHitBonuses.Where(row => selected.Contains(row.Talent)).ToArray();
            if (bonusRows.Length > 1)
            {
                throw new SimulationException("next-hit-bonus-unsupported",
                    $"{string.Join(" and ", bonusRows.Select(row => RuleOwnerName(pack.Pack, row.Talent)))} each add to the hit after a headshot kill, which one build can't have together");
            }
            bonusRow = bonusRows.FirstOrDefault();
            drivenKeys = [.. buffRules.Concat(statusRules).Concat(readings).Select(rule => rule.Key)
                .Concat(sequence.IsEmpty ? [] : [TargetProfileKey])
                .Concat(bandInputs)
                .Concat(buffRules.Where(rule => rule.Bank.Length > 0).Select(rule => rule.Bank))
                .Concat(options.Rotation is not null ? [GamePack.WeaponSlotSelector] : []).Distinct().Order(StringComparer.Ordinal)];
            // The cycle drives its key like a trigger's, so no other driven key may be it.
            if (cycleRow is not null)
            {
                if (drivenKeys.Contains(cycleRow.Key)) { throw new SimulationException("trigger-key-conflict", "A headshot cycle's key can't also be driven another way"); }
                drivenKeys = [.. drivenKeys.Append(cycleRow.Key).Order(StringComparer.Ordinal)];
            }
            // So does a mark's key (ADR 0036), which the validator keeps from every trigger rule.
            if (zoneKey.Length > 0)
            {
                if (drivenKeys.Contains(zoneKey)) { throw new SimulationException("trigger-key-conflict", "A zone mark's key can't also be driven another way"); }
                drivenKeys = [.. drivenKeys.Append(zoneKey).Order(StringComparer.Ordinal)];
            }
            fightBuffRules = buffRules;
            // A trigger talent on both rotated weapons runs one copy of its rules per weapon, each with its own keys,
            // pools and banks, and only the copy in hand drives the talent's keys (a holstered talent doesn't react).
            var copied = weaponTalents.Count == 2 ? weaponTalents.Values.First().Intersect(weaponTalents.Values.Last())
                .Where(talent => buffRules.Concat(statusRules).Any(rule => rule.Talent == talent)).ToHashSet(StringComparer.Ordinal) : [];
            if (copied.Count > 0)
            {
                if (statusRules.Any(rule => copied.Contains(rule.Talent)))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A status talent on both swapped weapons would apply to one shared target"); }
                // A shared rule (shareCopies, ADR 0019) isn't copied: it keeps the talent's own name, which both weapons hold, so the
                // weapon in hand drives its one key. A swap's swap goes to the weapon leaving and its swapped to the one drawn, so a
                // cancel on the first holds for the second (Unwavering), and the draw's gain replaces the other's (Back and Forth).
                bool Copies(SimTriggerDefinition rule) => copied.Contains(rule.Talent) && !rule.ShareCopies;
                var sharedRules = buffRules.Where(rule => rule.ShareCopies && copied.Contains(rule.Talent)).ToArray();
                var copiedRules = buffRules.Where(Copies).ToArray();
                // Holstered effects read the talent's keys while its other weapon is in hand. A copied rule's key is the copy in hand's, so
                // the holstered copy would read the wrong state; a talent whose rules are all shared has one key both weapons read
                // (Back and Forth on both, ADR 0019). With one key gating both kinds of its modifiers, one gain would count twice.
                var copiedTalents = copiedRules.Select(rule => rule.Talent).ToHashSet(StringComparer.Ordinal);
                static IEnumerable<string> Reads(StatModifier modifier) => modifier.RequiresContext?.Keys ?? [];
                foreach (var talent in pack.Pack.Talents.Where(talent => copied.Contains(talent.ID) && talent.Modifiers.Any(modifier => modifier.WhileHolstered)))
                {
                    var holsteredKeys = talent.Modifiers.Where(modifier => modifier.WhileHolstered).SelectMany(Reads).ToHashSet(StringComparer.Ordinal);
                    if (copiedTalents.Contains(talent.ID) || talent.Modifiers.Where(modifier => !modifier.WhileHolstered).SelectMany(Reads).Any(holsteredKeys.Contains))
                    { throw new SimulationException("rotation-shared-talent-unsupported", "A talent on both swapped weapons with holstered effects needs its rules shared (shareCopies), and its holstered and in-hand effects on different keys, so both copies read one state once"); }
                }
                var names = copiedRules.SelectMany(rule => new[] { rule.Key, rule.Bank }).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
                var pools = copiedRules.Select(rule => rule.Pool).Where(pool => pool.Length > 0).ToHashSet(StringComparer.Ordinal);
                // A shared rule can't hold a copy's key, pool or bank, nor gate on one (with whileKey or a context requirement): it has no
                // weapon of its own to pick the copy. Nor can it take a magazine-sized cap, which is each weapon's own.
                if (sharedRules.Any(rule => names.Contains(rule.Key) || names.Contains(rule.Bank) || pools.Contains(rule.Pool) || names.Contains(rule.WhileKey) ||
                    rule.RequiresContext?.Keys.Any(names.Contains) == true || rule.MaxMagazine > 0))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A rule shared by a talent's copies on both swapped weapons can't share a key, pool or gate with a copied rule, or take a magazine-sized cap"); }
                // The note says what the talent's shared rules do instead of one copy per weapon.
                sharedCopyNotes.AddRange(sharedRules.GroupBy(rule => rule.Talent, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{pack.Pack.Talents.FirstOrDefault(entry => entry.ID == group.Key)?.Name ?? group.Key} is on both weapons, and its shared rules aren't copied: they drive one key from the weapon in hand" +
                        (group.Any(rule => rule.CancelOn.Length > 0) ? ", so a cancel on the weapon a swap leaves holds for the one drawn" : "")));
                if (buffRules.Concat(statusRules).Concat(readings).Any(rule => !copied.Contains(rule.Talent) &&
                    (names.Contains(rule.Key) || names.Contains(rule.Bank) || pools.Contains(rule.Pool) || names.Contains(rule.WhileKey))))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A talent on both swapped weapons shares a key or pool with another talent"); }
                // Any selected talent's holstered effect reading a copied key would read the copy in hand's, not its own weapon's.
                if (pack.Pack.Talents.Any(talent => selected.Contains(talent.ID) && talent.Modifiers.Any(modifier => modifier.WhileHolstered &&
                    (modifier.RequiresContext?.Keys.Any(names.Contains) == true || names.Any(name => modifier.When.Contains(name, StringComparison.Ordinal))))))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A holstered effect reads a key a talent on both swapped weapons copies per weapon"); }
                foreach (var name in names) { copiedKeys[name] = weaponTalents.Keys.ToDictionary(slot => slot, slot => name + "@" + slot, StringComparer.Ordinal); }
                string For(string name, string slot) => name.Length == 0 ? "" : name + "@" + slot;
                fightBuffRules = [.. buffRules.Where(rule => !Copies(rule)), .. weaponTalents.Keys.SelectMany(slot => copiedRules.Select(rule => rule with
                {
                    Talent = For(rule.Talent, slot),
                    Key = For(rule.Key, slot),
                    Pool = For(rule.Pool, slot),
                    Bank = For(rule.Bank, slot),
                    // A copy's gate on its own talent's key (a primed draw, ADR 0019) reads its own copy's key.
                    WhileKey = names.Contains(rule.WhileKey) ? For(rule.WhileKey, slot) : rule.WhileKey
                }))];
                // Each weapon's copy reacts as that weapon's talent.
                foreach (var (slot, owned) in weaponTalents) { owned.UnionWith(copied.Select(talent => For(talent, slot))); }
            }
        }
        var resolved = BuildResolver.Resolve(pack, build, context);
        if (resolved.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
        { throw new SimulationException("resolve-failed", string.Join("; ", resolved.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))); }
        // A driven fight names the gear set settings it leaves as set (Striker's stacks), since nothing else in its result does.
        var setSettings = options.DriveSimpleBuffs ? UndrivenSetSettings(pack, build, resolved, context, drivenKeys, options.Rotation?.SecondSlot) : [];
        var active = context.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
        if (!resolved.Loadout.Equipped.Any(item => item.WeaponSlot && item.SlotId == active))
        { throw new SimulationException("weapon-required", "Select an equipped active weapon"); }
        var stats = resolved.Calculation.Stats;
        var initialWeapon = ShotProfile.Read(resolved, targeted, firstDistance, zones);
        var incoming = options.IncludeIncoming ? EnemyFireSchedule.Create(resolved) : null;
        // The profile of a fight's chosen target, which fires when no sequence sets each target's own.
        var chosenProfile = resolved.Assumptions.Numeric.TryGetValue(TargetProfileKey, out var chosenTarget) ? (int)chosenTarget : -1;
        // Statuses enemies' hits put on the player (ADR 0018), by target profile, read from the build as the fight starts against that
        // profile: the duration shortened by the row's resistances (not below its floor), and the tick amount. One the resistances reach
        // 1 for doesn't go on, though the fight still reports statuses on the player, as none.
        var enemyStatusRows = new Dictionary<int, List<(string Status, double Duration, double Tick, double Period, bool TickOnApply)>>();
        var reportsPlayerStatuses = incoming is not null && !pack.Pack.Simulation.EnemyStatuses.IsDefaultOrEmpty;
        if (reportsPlayerStatuses)
        {
            var profileStats = new Dictionary<int, ImmutableDictionary<string, double>>();
            ImmutableDictionary<string, double> StatsAgainst(int profile)
            {
                if (profile == chosenProfile) { return stats; }
                if (!profileStats.TryGetValue(profile, out var read))
                {
                    var against = BuildResolver.Resolve(pack, build, context.SetItem(TargetProfileKey, profile.ToString(CultureInfo.InvariantCulture)));
                    if (against.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
                    { throw new SimulationException("resolve-failed", $"The build doesn't resolve against target profile {profile}"); }
                    profileStats[profile] = read = against.Calculation.Stats;
                }
                return read;
            }
            foreach (var row in pack.Pack.Simulation.EnemyStatuses)
            {
                var rowStats = StatsAgainst(row.Target);
                double StatusStat(string id) => rowStats.TryGetValue(id, out var value) && double.IsFinite(value) ? value :
                    throw new SimulationException("enemy-status-invalid", $"A status on the player reads {id}, which has no finite value");
                var resisted = Math.Clamp(row.Resistances.IsDefaultOrEmpty ? 0 : row.Resistances.Sum(StatusStat), 0, 1);
                if (resisted >= 1) { continue; }
                var tick = row.Tick.Length == 0 ? 0 : StatusStat(row.Tick);
                if (tick < 0) { throw new SimulationException("enemy-status-invalid", $"A status on the player ticks for a negative {row.Tick}"); }
                if (!enemyStatusRows.TryGetValue(row.Target, out var rows)) { enemyStatusRows[row.Target] = rows = []; }
                rows.Add((row.Status, Math.Max(row.MinDuration, row.Duration * (1 - resisted)), tick, row.Period, row.TickOnApply));
            }
        }
        // A hit chance that follows each target's distance moves the DPS with it, not the cadence; the validator keeps triggers off it.
        var hitRateFollows = bandInputs.Contains(EnemyFireSchedule.HitRateInput);
        // Checked here, so whether a band is refused never depends on which targets a fight reaches.
        foreach (var meters in distanceBands.Keys)
        {
            if (!incoming!.SameClockAndPools(EnemyFireSchedule.Create(BuildResolver.Resolve(pack, build, AtDistance(context, meters))), hitRateFollows))
            { throw new SimulationException("incoming-state-unsupported", "A distance default that changes enemy cadence or the player's pools can't follow each target"); }
        }
        var shieldProfile = options.IncludeShield ? ShieldProfile.Read(pack.Pack, resolved, active, requireAllowed: options.Rotation is null) : null;
        // When a shield allows only one of two rotated weapons: that weapon. A raised shield then forces it at the next
        // shot or after the current reload, and a break returns the build's weapon the same way (owner, 2026-10-31).
        string? shieldWeapon = null;
        // Set repairs no fight reaches run without the repair (ADR 0021), and the result says why. A talent that replaces the armor kit
        // (the pack's armorKit) is reached in a fight with kits, so it steps aside only without them.
        var kitReplacers = pack.Pack.Simulation.ArmorKit?.ReplacedByTalents ?? [];
        var unreachedRepairs = UnreachedRepairSets.Where(repair => !(options.ArmorKits is not null && kitReplacers.Contains(repair.Talent))).ToImmutableArray();
        // A talent whose ability is the build's kit (ADR 0021): in a fight with kits its rules make its repair, so its repair stats are
        // modelled, not refused, and not noted as unreached. Only one ability can be the kit.
        var fullSetTalents = options.ArmorKits is null ? [] : SetBonusSelection.FullSetTalents(pack.Pack, resolved.ActiveSets).ToHashSet(StringComparer.Ordinal);
        var kitAbilities = options.ArmorKits is null ? [] : (pack.Pack.Simulation.ArmorKit?.Abilities ?? []).Where(ability => selected.Contains(ability.Talent) || fullSetTalents.Contains(ability.Talent)).ToArray();
        if (kitAbilities.Length > 1)
        {
            throw new SimulationException("armor-kits-unsupported",
                $"{string.Join(" and ", kitAbilities.Select(ability => RuleOwnerName(pack.Pack, ability.Talent)))} each make their ability the armor kit, which one build can't have together");
        }
        var kitAbility = kitAbilities.FirstOrDefault();
        // The cycle's repair stat, and stats built on it, are modelled, so they don't refuse a fight under enemy fire.
        var cycleStats = cycleRow is null ? [] : StatsReading(pack.Pack, cycleRow.Repair);
        double CycleStat(string id)
        {
            var value = resolved.Calculation.Stats.GetValueOrDefault(id);
            return double.IsFinite(value) && value >= 0 ? value : throw new SimulationException("headshot-cycle-invalid", $"A headshot cycle reads {id}, which has no finite value of 0 or more");
        }
        var cycleRepair = cycleRow is null ? 0 : CycleStat(cycleRow.Repair);
        var cycleCap = cycleRow is null ? 0 : CycleStat(cycleRow.BonusArmorCap);
        var cycleAllowance = cycleRow is null ? 0 : (int)Math.Min(100, Math.Floor(CycleStat(cycleRow.Allowance)));
        var unreachedStats = UnreachedRepairSets.Where(repair => unreachedRepairs.Contains(repair) || repair.Talent == kitAbility?.Talent)
            .SelectMany(repair => repair.Stats).Concat(cycleStats).ToImmutableHashSet(StringComparer.Ordinal);
        // Every state the fight reads, its setup's included, notes the unreached repairs it carries.
        var unreachedSeen = new HashSet<string>(StringComparer.Ordinal);
        // Clutch, Doctor Home and Preservation repair through their pack rules (R43); recovery here is the generic kind.
        RecoveryProfile ReadRecovery(ResolvedBuild current)
        {
            var profile = ReadIncomingRecovery(current, unreachedStats);
            foreach (var repair in unreachedRepairs.Where(repair => repair.Stats.Any(stat => current.Calculation.Stats.GetValueOrDefault(stat) != 0))) { unreachedSeen.Add(repair.Talent); }
            if (options.IncludeSkills && profile.WeaponKillArmor > 0)
            { throw new SimulationException("skill-kill-repair-unsupported", "Unmodeled repair-per-kill sources require eligibility tracking"); }
            return profile;
        }
        var initialRecovery = incoming is not null ? ReadRecovery(resolved) : null;
        // Status tick amounts by status, and talent debuff totals by their stat id, read from each resolved state.
        // Stats adding to the armor factor of ticks and skill rates (R20) are read with the state, like the ticks themselves.
        var statusArmorBonuses = dotRules.Where(rule => rule.ArmorBonus.Length > 0).ToImmutableDictionary(rule => rule.Status, rule => rule.ArmorBonus, StringComparer.Ordinal);
        var rateArmorBonuses = options.IncludeSkills ? pack.Pack.Simulation.Skills.SelectMany(rule => new[] { rule.RateArmorBonus, rule.StatusArmorBonus }).Where(stat => stat.Length > 0) : [];
        ImmutableDictionary<string, double> ReadStatusTicks(ResolvedBuild current) => dotRules.Select(rule => (Key: rule.Status, Stat: rule.Tick))
            .Concat(talentDamageRules.Select(rule => (Key: rule.Total, Stat: rule.Total)))
            .Concat(blastRules.Where(rule => rule.Amount.Length > 0).Select(rule => (Key: rule.Amount, Stat: rule.Amount)))
            .Concat(statusArmorBonuses.Values.Concat(rateArmorBonuses).Select(stat => (Key: stat, Stat: stat))).DistinctBy(pair => pair.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(pair => pair.Key, pair =>
        {
            var tick = current.Calculation.Stats.GetValueOrDefault(pair.Stat);
            if (!double.IsFinite(tick) || tick < 0) { throw new SimulationException("status-damage-invalid", $"Invalid {pair.Stat} tick damage"); }
            return tick;
        }, StringComparer.Ordinal);
        var initialTicks = ReadStatusTicks(resolved);
        // Fixed assumptions have no cooldown, so a depletion repair would restore armor on every break.
        if (!options.DriveSimpleBuffs && initialRecovery?.BreakArmor > 0)
        { throw new SimulationException("incoming-repair-unsupported", "Armor-depletion repair needs driven triggers for its cooldown"); }
        var armorKit = options.ArmorKits is null ? null : pack.Pack.Simulation.ArmorKit ?? throw new SimulationException("armor-kits-unsupported", "This pack doesn't say what an armor kit does");
        if (armorKit is not null)
        {
            if (DummyFightOptions.ArmorKitsProblem(options.IncludeIncoming, options.DriveSimpleBuffs) is { } kitProblem)
            { throw new SimulationException("armor-kits-unsupported", kitProblem); }
            if (armorKit.ReplacedBySpecializations.Contains(build.SpecializationId) ||
                armorKit.ReplacedByTalents.Any(id => (selected.Contains(id) || fullSetTalents.Contains(id)) && id != kitAbility?.Talent))
            { throw new SimulationException("armor-kits-unsupported", "This build replaces the armor kit, which fights don't model yet; run it without kits"); }
        }
        // The ability's cooldown, read as the fight starts.
        var abilityCooldown = kitAbility is null ? 0 : resolved.Calculation.Stats.GetValueOrDefault(kitAbility.Cooldown);
        if (kitAbility is not null && (!double.IsFinite(abilityCooldown) || abilityCooldown <= 0 || abilityCooldown > 600))
        { throw new SimulationException("armor-kits-unsupported", $"{RuleOwnerName(pack.Pack, kitAbility.Talent)}'s cooldown ({kitAbility.Cooldown}) must be above 0 and up to 600 seconds"); }
        var instantKit = kitAbility is null ? null : armorKit!.Instant;
        if (options.FocusSeconds is not null && DummyFightOptions.FocusProblem(options.EnemiesAtOnce) is { } focusProblem)
        { throw new SimulationException("focus-unsupported", focusProblem); }
        if (options.EnemySpacing is not null && DummyFightOptions.EnemySpacingProblem(options.EnemiesAtOnce) is { } spacingProblem)
        { throw new SimulationException("enemy-spacing-unsupported", spacingProblem); }
        // Blasts, spreads and pass-ons reach other enemies only with several at once and a spacing; otherwise they find nobody, and
        // the result says so. With any of them, a waiting enemy can die out of order, so the next arrival follows the group.
        // The talents whose effects reach other enemies (ADR 0013), for the reach and its note.
        // A selected skill whose impacts reach an area (ADR 0016) reaches other enemies too.
        // A skill whose gate the settings don't hold steps aside (ADR 0027), so it counts as not selected here.
        var selectedSkills = build.ActiveSkillSelections.Values.Where(skill => pack.Pack.Simulation.Skills.All(rule => rule.Skill != skill ||
            ResolveAssumptions.Matches(rule.RequiresContext, context))).ToHashSet(StringComparer.Ordinal);
        // An impact's area always reaches; a control status's, or a status window's (a cloud, ADR 0024), only where statuses go on other
        // enemies (with talent triggers, ADR 0017).
        string[] areaSkills = options.IncludeSkills
            ? [.. pack.Pack.Simulation.Skills.Where(rule => selectedSkills.Contains(rule.Skill) && (rule.Targets.Length > 0 || rule.Area.Length > 0 &&
                (rule.Impact.Length > 0 || (rule.ControlStatus || rule.IsCloud) && options.DriveSimpleBuffs)))
                .Select(rule => rule.Skill)] : [];
        var impactAreas = options.IncludeSkills && pack.Pack.Simulation.Skills.Any(rule => rule.Area.Length > 0 && rule.Impact.Length > 0 && selectedSkills.Contains(rule.Skill));
        var cloudAreas = options.IncludeSkills && options.DriveSimpleBuffs && pack.Pack.Simulation.Skills.Any(rule => rule.IsCloud && selectedSkills.Contains(rule.Skill));
        var areaFalls = options.IncludeSkills && pack.Pack.Simulation.Skills.Any(rule => rule.AreaFull.Length > 0 && selectedSkills.Contains(rule.Skill));
        // A selected control skill whose row puts its status on (ADR 0017), in a fight that holds statuses (one with talent triggers).
        var controlSkills = targeted && options.IncludeSkills && options.DriveSimpleBuffs &&
            pack.Pack.Simulation.Skills.Any(rule => rule.ControlStatus && selectedSkills.Contains(rule.Skill));
        // Selected skills whose effects come after the use, or whose impacts stop at armor (ADR 0029), for their notes.
        var delayedSkills = options.IncludeSkills ? pack.Pack.Simulation.Skills.Where(rule => rule.ImpactDelay > 0 && selectedSkills.Contains(rule.Skill)).ToArray() : [];
        // A dummy has no armor, and counts skill damage whole, so the note is a targeted fight's.
        var killSkills = targeted && options.IncludeSkills ? pack.Pack.Simulation.Skills.Where(rule => rule.KillChance.Length > 0 && selectedSkills.Contains(rule.Skill)).ToArray() : [];
        var armorOnlySkills = targeted && options.IncludeSkills && pack.Pack.Simulation.Skills.Any(rule => rule.ArmorOnly && selectedSkills.Contains(rule.Skill));
        // A selected skill repairing the player at each impact (ADR 0028), for its note.
        var repairSkills = options.IncludeSkills && pack.Pack.Simulation.Skills.Any(rule => rule.ImpactRepair.Length > 0 && selectedSkills.Contains(rule.Skill));
        // A selected skill striking several enemies at once (ADR 0027), for its note.
        var targetSkills = options.IncludeSkills && pack.Pack.Simulation.Skills.Any(rule => rule.Targets.Length > 0 && selectedSkills.Contains(rule.Skill));
        string[] reachTalents = [.. blastRules.Select(rule => rule.Talent).Concat(spreadRules.Select(rule => rule.Talent)).Concat(passOnRules.Select(rule => rule.Talent))
            .Concat(redirectRules.Select(rule => rule.Talent)).Concat(areaSkills).Distinct(StringComparer.Ordinal)];
        var reachActive = targeted && reachTalents.Length > 0 && options.EnemiesAtOnce is not null && options.EnemySpacing is not null;
        // A pass-on's reach and share may come from stats (Proliferation's 15 m and 75%), read once, at the fight's start, and only when
        // it can reach someone: a fight it plays no part in isn't refused for its values.
        if (reachActive) { passOnRules = [.. passOnRules.Select(rule => PassOnFromStats(rule, stats))]; }
        // A trap reaches by tag, at any distance, so it needs several enemies but no spacing.
        var trapActive = targeted && !trapRules.IsEmpty && options.EnemiesAtOnce is not null;
        var spacing = options.EnemySpacing ?? 0;
        if (options.EnemiesShrink && DummyFightOptions.EnemiesShrinkProblem(options.EnemiesAtOnce, options.ClearKills) is { } shrinkProblem)
        { throw new SimulationException("enemies-at-once-unsupported", shrinkProblem); }
        // How many enemies a fight has in all, when it has an end; otherwise the next is always there.
        var enemyTotal = options.EnemiesShrink ? options.ClearKills : null;
        // With a target sequence each target in play sets enemy fire, so attempts are timed as the fight runs.
        var perTargetFire = incoming is not null && !sequence.IsEmpty;
        // Otherwise every fight shares one enemy cadence, so its attempt times (and any work-limit refusal) come at setup.
        var enemyShotTimes = perTargetFire || options.EnemiesAtOnce is not null ? [] : incoming?.ShotTimeArray(options.Duration, cancellationToken) ?? [];
        if (options.EnemiesAtOnce is { } enemiesAtOnce)
        {
            if (DummyFightOptions.EnemiesAtOnceProblem(options.IncludeIncoming, options.DriveSimpleBuffs, incoming?.Attackers ?? 1) is { } enemiesProblem)
            { throw new SimulationException("enemies-at-once-unsupported", enemiesProblem); }
            // A sequence's own profiles are the ones that fire, and each is checked below.
            if (sequence.IsEmpty) { incoming!.CheckAttempts(options.Duration, enemiesAtOnce); }
        }
        // Checked once at setup, so whether a run fails never depends on its random rolls.
        combat?.ValidatePelletCapacity((int)initialWeapon.Pellets);
        WeaponRotation? template = null;
        var profiles = new Dictionary<string, ShotProfile>(StringComparer.Ordinal) { [active] = initialWeapon };
        if (options.Rotation is { } plan)
        {
            if (!targeted && plan.Mode == WeaponSwapMode.AfterKill)
            { throw new SimulationException("target-required", "Kill-based rotation requires targets"); }
            template = WeaponRotation.Create(pack, build, context, active, plan.SecondSlot, plan.Mode, plan.SwapTime);
            profiles[plan.SecondSlot] = ShotProfile.Read(template.Resolution(plan.SecondSlot), targeted, firstDistance, zones);
            if (shieldProfile is not null)
            {
                var allowsActive = ShieldProfile.Allows(pack.Pack, resolved, active);
                var allowsSecond = ShieldProfile.Allows(pack.Pack, template.Resolution(plan.SecondSlot), plan.SecondSlot);
                if (!allowsActive && !allowsSecond)
                { throw new SimulationException("shield-weapon-unsupported", "Neither swapped weapon is allowed by this shield"); }
                if (allowsActive != allowsSecond) { shieldWeapon = allowsActive ? active : plan.SecondSlot; }
            }
            combat?.ValidatePelletCapacity((int)profiles[plan.SecondSlot].Pellets);
        }
        // Without driven state nothing resolves as a fight goes, so each weapon's bonuses are read here.
        var heldTicks = template is null || options.DriveSimpleBuffs ? null : profiles.Keys.ToDictionary(slot => slot, slot => ReadStatusTicks(template.Resolution(slot)), StringComparer.Ordinal);
        // Each weapon's driven state resolves with that weapon in hand.
        var handContexts = profiles.Keys.ToDictionary(slot => slot, slot => context.SetItem(GamePack.WeaponSlotSelector, slot), StringComparer.Ordinal);
        // A weapon event carries its weapon's item tags, which rules with weaponTags read.
        var weaponTags = profiles.Keys.ToDictionary(slot => slot, slot => (IReadOnlySet<string>)(pack.Pack.Items
            .FirstOrDefault(item => item.ID == build.Equipment.GetValueOrDefault(slot))?.Tags.ToHashSet(StringComparer.Ordinal) ?? []), StringComparer.Ordinal);
        // A magazine-sized stack cap (Ardent's meter) reads its own weapon's magazine; copies of a talent are per weapon.
        var magazines = new Dictionary<string, double>(StringComparer.Ordinal);
        // The other rotated weapon's tags for each weapon, for a gate on differing weapon types (Versatile, ADR 0019); none with one weapon.
        var otherWeaponTags = profiles.Keys.ToDictionary(slot => slot, slot => profiles.Keys.Where(other => other != slot).Select(other => weaponTags[other]).FirstOrDefault(),
            StringComparer.Ordinal);
        foreach (var (slot, owned) in weaponTalents.Where(pair => profiles.ContainsKey(pair.Key)))
        { foreach (var talent in owned) { magazines[talent] = profiles[slot].Magazine; } }
        // Such a cap is read once, so a state changing the magazine isn't scheduled with one (ADR 0025).
        var magazineCapped = buffRules.Any(rule => rule.MaxMagazine > 0);
        // Gated status rules (Ardent's burn) read the driven keys at their event, and a bullet's events read them at its firing.
        var gated = statusRules.Any(rule => rule.WhileKey.Length > 0);
        var holdingTalents = profiles.Keys.ToDictionary(slot => slot, slot => template is null ? null :
            (IReadOnlySet<string>)new HashSet<string>(gearTalents.Concat(weaponTalents.GetValueOrDefault(slot) ?? Enumerable.Empty<string>()), StringComparer.Ordinal), StringComparer.Ordinal);
        // A next-hit bonus's cap, for each weapon, read as the fight starts (with one weapon, from the fight's own resolution).
        var bonusCaps = bonusRow is null ? null : profiles.Keys.ToDictionary(slot => slot, slot =>
        {
            var value = (template?.Resolution(slot) ?? resolved).Calculation.Stats.GetValueOrDefault(bonusRow.Cap);
            return double.IsFinite(value) && value >= 0 ? value
                : throw new SimulationException("next-hit-bonus-invalid", $"A next-hit bonus reads {bonusRow.Cap}, which has no finite value of 0 or more");
        }, StringComparer.Ordinal);
        var armor = targeted ? WeaponTiming.Require(stats, "enemy_target_armor") : 0;
        var health = targeted ? WeaponTiming.Require(stats, "enemy_target_health") : 1;
        if (health <= 0) { throw new SimulationException("target-invalid", "Targets need positive health"); }
        // Each profile in a target sequence resolves its own pools and its own enemy fire.
        var targetInput = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == TargetProfileKey);
        var sequencePools = new Dictionary<int, (double Armor, double Health)>();
        var sequenceFire = new Dictionary<int, EnemyFireSchedule>();
        // Each weapon's skill schedule against each profile, with the same timing as the chosen profile's (P10).
        var sequenceSkills = new Dictionary<string, Dictionary<int, SkillDamageSchedule>>(StringComparer.Ordinal);
        var skillSchedule = options.IncludeSkills ? SkillDamageSchedule.Create(pack.Pack, resolved.ActiveSkillPreviews, options.IncludeShield, context, FightDistance(options), options.IncludeIncoming, options.DriveSimpleBuffs) : null;
        // Each weapon's skills as fights start. A second weapon is held to its own timing against mixed targets.
        var heldSkills = new Dictionary<string, SkillDamageSchedule>(StringComparer.Ordinal);
        if (skillSchedule is not null)
        {
            heldSkills[active] = skillSchedule;
            if (options.Rotation is { } skillRotation)
            { heldSkills[skillRotation.SecondSlot] = SkillDamageSchedule.Create(pack.Pack, template!.Resolution(skillRotation.SecondSlot).ActiveSkillPreviews, options.IncludeShield, context, FightDistance(options), options.IncludeIncoming, options.DriveSimpleBuffs); }
        }
        // A set talent's rule can reach what a fight holds fixed: Apex Engine's stacks raise Skill Tier, and a shield doesn't follow. So can
        // Energize's kit, whose Overcharge lifts a shield even at tier 6 (R64), and any rule waiting on a stat. A fight would only be refused
        // when the first stack lands, a kit is used or the gate opens, so the rolls would decide. It's checked here, at the rule's height, so
        // they can't. Skills follow, so they need no check (ADR 0012).
        if (shieldProfile is not null || incoming is not null)
        {
            foreach (var rule in pack.Pack.Simulation.Triggers.Where(rule => setTalents.Contains(rule.Talent) ||
                options.ArmorKits is not null && options.DriveSimpleBuffs && rule.On is "armor_kit" or "kit_ability" && gearTalents.Contains(rule.Talent) ||
                rule.WhileStatAtLeast is not null && buffRules.Contains(rule)))
            {
                var name = RuleOwnerName(pack.Pack, rule.Talent);
                // A rule on a weapon reaches the build only with that weapon in hand.
                foreach (var held in rule.WhileStatAtLeast is null ? [context] : handContexts.Values.ToArray())
                {
                    // A gate that every other rule in the fight at its height still can't open never fires, so it's no reason to refuse
                    // (Future Perfect below tier 6). That bounds only these rules; a gate opened another way meets the check as it resolves.
                    if (rule.WhileStatAtLeast is { } gate)
                    {
                        var height = held;
                        foreach (var other in buffRules.Where(other => other.Key != rule.Key))
                        { height = height.SetItem(other.Key, other.Kind == "stacks" ? other.Max.ToString(CultureInfo.InvariantCulture) : "true"); }
                        if (BuildResolver.Resolve(pack, build, height).Calculation.Stats.GetValueOrDefault(gate.Stat) < gate.Value) { continue; }
                    }
                    var reached = BuildResolver.Resolve(pack, build, held.SetItem(rule.Key, rule.Kind == "stacks" ? rule.Max.ToString(CultureInfo.InvariantCulture) : "true"));
                    if (shieldProfile is not null && ShieldProfile.Read(pack.Pack, reached, active, requireAllowed: options.Rotation is null) != shieldProfile)
                    { throw new SimulationException("shield-state-unsupported", $"{name} changes this build's shield during a fight, which fights can't follow yet; run it without the shield"); }
                    if (incoming is not null) { _ = ReadRecovery(reached); }
                }
            }
        }
        // Each profile is checked with every weapon the fight uses: both weapons must give it the same pools, and its skills must
        // keep the chosen profile's timing; their damage may follow the target, and the weapon in hand.
        string[] sequenceWeapons = options.Rotation is { } rotated ? [active, rotated.SecondSlot] : [active];
        foreach (var profile in sequence.Distinct())
        {
            if (targetInput is null || profile < 1 || !targetInput.Accepts(profile))
            { throw new SimulationException("target-sequence-invalid", $"Target profile {profile} is not one of the pack's enemy targets"); }
            foreach (var weaponSlot in sequenceWeapons)
            {
                var profiled = BuildResolver.Resolve(pack, build, context.SetItem(TargetProfileKey, profile.ToString(CultureInfo.InvariantCulture))
                    .SetItem(GamePack.WeaponSlotSelector, weaponSlot));
                if (profiled.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
                { throw new SimulationException("resolve-failed", $"Target profile {profile} failed resolution"); }
                var profileArmor = WeaponTiming.Require(profiled.Calculation.Stats, "enemy_target_armor");
                var profileHealth = WeaponTiming.Require(profiled.Calculation.Stats, "enemy_target_health");
                if (profileArmor < 0 || profileHealth <= 0) { throw new SimulationException("target-invalid", $"Target profile {profile} needs positive health"); }
                if (sequencePools.TryGetValue(profile, out var first) && first != (profileArmor, profileHealth))
                { throw new SimulationException("target-pool-mismatch", $"Rotation must resolve the same pools for target profile {profile} with both weapons"); }
                sequencePools[profile] = (profileArmor, profileHealth);
                if (perTargetFire)
                {
                    var fire = EnemyFireSchedule.Create(profiled);
                    // The player is one player, and a silent target would leave no attempt to time the next from.
                    if (!incoming!.SamePlayer(fire))
                    { throw new SimulationException("incoming-state-unsupported", "Player armor, health or attackers that change with the target profile require dynamic scheduling"); }
                    if (sequenceFire.Count > 0 && (sequenceFire.Values.First().Interval is null) != (fire.Interval is null))
                    { throw new SimulationException("incoming-state-unsupported", "A target sequence needs every target to fire, or none"); }
                    if (sequenceFire.TryGetValue(profile, out var otherFire) && !otherFire.SameClockAndPools(fire))
                    { throw new SimulationException("incoming-state-unsupported", $"Enemy fire from target profile {profile} differs by weapon"); }
                    // As without a sequence, a target's cadence too fast for a fight, or recovery that can't run, is refused here, so
                    // the outcome never depends on which targets a fight reaches.
                    fire.CheckAttempts(options.Duration, options.EnemiesAtOnce ?? 1);
                    _ = ReadRecovery(profiled);
                    // The weapon in hand's, which the fight starts with: the first of the fight's weapons.
                    sequenceFire.TryAdd(profile, fire);
                }
                if (skillSchedule is not null)
                {
                    var profileSkills = SkillDamageSchedule.Create(pack.Pack, profiled.ActiveSkillPreviews, options.IncludeShield, context, FightDistance(options), options.IncludeIncoming, options.DriveSimpleBuffs);
                    if (!heldSkills[weaponSlot].SameTiming(profileSkills))
                    { throw new SimulationException("target-sequence-unsupported", "Skill timing, or which kinds of damage a skill deals, changes with the target profile, which mixed targets can't model yet"); }
                    if (!sequenceSkills.TryGetValue(weaponSlot, out var weaponSkills)) { sequenceSkills[weaponSlot] = weaponSkills = []; }
                    weaponSkills[profile] = profileSkills;
                }
            }
        }
        var diagnostics = resolved.Diagnostics;
        if (options.Rotation is { } second)
        {
            var other = template!.Resolution(second.SecondSlot);
            diagnostics = [.. diagnostics.Concat(other.Diagnostics).Distinct()];
            if (targeted && (other.Calculation.Stats.GetValueOrDefault("enemy_target_armor") != armor || other.Calculation.Stats.GetValueOrDefault("enemy_target_health") != health))
            { throw new SimulationException("target-pool-mismatch", "Rotation must resolve the same target pools for both weapons"); }
        }
        // For fights where no rule can change them, each weapon's amounts against each profile. The weapon in hand decides at each deployment.
        foreach (var (weaponSlot, against) in sequenceSkills) { heldSkills[weaponSlot] = heldSkills[weaponSlot].WithAmounts(against); }
        skillSchedule = skillSchedule is null ? null : heldSkills[active];
        ImmutableArray<double> skillCuts = [];
        if (options.IncludeSkills && options.IncludeIncoming)
        {
            // Several enemies sharing one cadence keep their clocks through kills, so their attempts are known here too. A group
            // running out loses clocks, so for it these are every attempt it could make.
            var attemptTimes = options.EnemiesAtOnce is { } steadyEnemies && sequence.IsEmpty && incoming?.Interval is { } steady
                ? EnemyGroup.SteadyTimes(steadyEnemies, steady, options.Duration) : enemyShotTimes;
            skillCuts = attemptTimes;
        }
        foreach (var held in heldSkills.Values) { held.CheckWork(options.Duration, skillCuts); }
        var skillFights = skillSchedule?.Fights(options.Duration, skillCuts, cancellationToken);
        // ADR 0031: a buff hive's buff on the player lasts as long as its charges are spaced for, or the charges would leave gaps or
        // overlap: its fixed duration, or its durationStat as the fight starts, against the hive's chargeFor then. A hive charging skills
        // spaces them by target, not by its buff. The buff's duration is read once, as the fight starts, so the charges keep that spacing
        // through the fight, even where a later use reads a longer chargeFor (a tier raised mid-fight).
        var chargeBuffSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var charger in skillFights?.Plans.Where(entry => entry.ChargeFor > 0 && !entry.ChargeSkills) ?? [])
        {
            foreach (var rule in fightBuffRules.Where(rule => rule.Talent == charger.Skill))
            {
                var seconds = rule.DurationStat.Length == 0 ? rule.Duration : stats.GetValueOrDefault(rule.DurationStat, double.NaN);
                if (!(Math.Abs(seconds - charger.ChargeFor) <= 1e-9))
                { throw new SimulationException("trigger-unsupported", string.Create(CultureInfo.InvariantCulture, $"{charger.Skill}'s buff lasts {seconds:G} s, but its charges are spaced for {charger.ChargeFor:G} s")); }
                chargeBuffSeconds[charger.Skill] = seconds;
            }
        }
        // ADR 0022: skills the pack says play no part in a fight step aside, by slot. They're never used, so a use condition on their slot
        // is dropped.
        var asideSkills = skillSchedule?.Aside ?? ImmutableDictionary<string, SimSkillAside>.Empty;
        var skillUse = options.SkillUse.RemoveRange(asideSkills.Keys);
        var droppedUses = options.SkillUse.Keys.Where(asideSkills.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        // ADR 0014: each conditioned slot needs a damaging skill there, and a key condition a key the build's rules drive.
        if (!skillUse.IsEmpty)
        {
            if (skillFights is null) { throw new SimulationException("skill-use-unsupported", "Skill use conditions need damaging skills in the fight"); }
            foreach (var (slot, condition) in skillUse.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (skillFights.IndexOf(slot) < 0) { throw new SimulationException("skill-use-unsupported", $"Skill slot {slot} has no damaging skill in this build"); }
                if (condition.When == SkillUseCondition.KeyOn && !(options.DriveSimpleBuffs && drivenKeys.Contains(condition.Key)))
                { throw new SimulationException("skill-use-unsupported", $"A skill use can wait only for a key the build's rules drive, with talent triggers on: {condition.Key}"); }
                if (condition.When == SkillUseCondition.EnemiesAtLeast && (options.EnemiesAtOnce is not { } atOnce || condition.Count > atOnce))
                { throw new SimulationException("skill-use-unsupported", $"A skill use waiting for enemies needs several enemies at once, at least as many as its count: {condition.Count}"); }
            }
        }
        var skillUseTotals = skillUse.Keys.ToDictionary(slot => slot, _ => (Uses: 0d, Held: 0d), StringComparer.Ordinal);
        // ADR 0030: slots used only once the player's armor falls below a share, under enemy fire, with their uses over the fights.
        var armorWaitSlots = options.IncludeIncoming && skillFights is not null
            ? skillFights.Plans.Where(plan => plan.UseArmorBelow > 0).ToDictionary(plan => plan.Slot, StringComparer.Ordinal)
            : new Dictionary<string, SkillDamageSchedule.Plan>(StringComparer.Ordinal);
        var armorWaitUses = armorWaitSlots.Keys.ToDictionary(slot => slot, _ => 0, StringComparer.Ordinal);
        var skillStates = new Dictionary<string, SkillDamageSchedule>(StringComparer.Ordinal);
        var killTimes = ImmutableArray.CreateBuilder<double>();
        double kills = 0;
        // Each fight's time to its Nth kill, when a kill count is asked for and the fight reached it.
        var clearTimes = new List<double>();
        // Each fight's mean time from a target's arrival to its death, for fights with a kill.
        var killTimeMeans = new List<double>();
        var fights = options.Mode == SimulationMode.Average ? 1 : options.Iterations;
        var timeline = new double[(int)Math.Ceiling(options.Duration)];
        var events = ImmutableArray.CreateBuilder<DummyEvent>();
        var dps = new double[fights];
        var skillStatistics = options.IncludeSkills ? new SkillStatistics(fights) : null;
        var statusDps = dotRules.IsEmpty ? null : new double[fights];
        var talentDps = talentDamageRules.IsEmpty && !(reachActive && !blastRules.IsEmpty) ? null : new double[fights];
        var totalDps = new double[fights];
        long work = 0;
        double shots = 0, reloads = 0, swaps = 0;
        double zoneHits = 0;
        double enemyAttempts = 0, enemyHits = 0, armorBreaks = 0, downed = 0, blockedShots = 0, removedHits = 0, drawnHits = 0, revives = 0, kitsUsed = 0, kitsCancelled = 0, kitRepaired = 0, abilityUses = 0;
        var incomingDamage = new double[fights];
        var statusDamageTaken = new double[fights];
        var playerStatusSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var armorRepairs = new double[fights];
        var healthRepairs = new double[fights];
        var shieldRepairs = new double[fights];
        var shieldAbsorbed = new double[fights];
        // A talent's shield drain or repair from damage (ADR 0026: Symbiosis) on any weapon the fight holds, under fire.
        var heldResolutions = template is null ? [resolved] : profiles.Keys.Select(template.Resolution).ToArray();
        bool Carries(string stat) => incoming is not null && heldResolutions.Any(held => held.Calculation.Stats.GetValueOrDefault(stat) > 0);
        var shieldTalent = Carries(ShieldDrainStat) || Carries(ShieldDamageShareStat);
        // The drain ticks on the fight's whole seconds, only with a shield to drain.
        var drainUsed = shieldProfile is not null && Carries(ShieldDrainStat);
        var shieldDrained = new double[fights];
        double shieldBreaks = 0, shieldDeployments = 0, shieldSwitches = 0;
        var breakTimes = new List<double>();
        var downTimes = new List<double>();
        var statusSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var triggerSeconds = new Dictionary<string, TimedTriggerDuration>(StringComparer.Ordinal);
        var cycleSeconds = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var magazineSeconds = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        // Driven states repeat across shots and fights, so each distinct one resolves once.
        var resolvedStates = new Dictionary<string, (ShotProfile Profile, EnemyFireSchedule? Enemy, RecoveryProfile? Recovery, ImmutableDictionary<string, double> Ticks, ImmutableDictionary<string, double> Stats)>(StringComparer.Ordinal);
        string StateKey(ImmutableDictionary<string, string> driven) =>
            string.Join('\u001f', drivenKeys.Select(key => driven.TryGetValue(key, out var value) ? key + '=' + value : key));
        var truncated = false;
        for (var fight = 0; fight < fights; fight++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skills = skillFights?.NewFight();
            // ADR 0031 stage 2c: a hive charging skills pulses through a fight driving its buffs, acting only while holstered.
            for (var index = 0; skills is not null && options.DriveSimpleBuffs && index < skills.Plans.Length; index++)
            {
                var pulsing = skills.Plans[index];
                if (pulsing.HolsteredEvery <= 0) { continue; }
                var count = Math.Ceiling(options.Duration / pulsing.HolsteredEvery);
                if (!(count <= SkillEventStream.EventLimit)) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
                var pulses = new SkillDamageEvent[(int)count];
                for (var at = 0; at < pulses.Length; at++) { pulses[at] = new(at * pulsing.HolsteredEvery, 0, pulsing.Slot, SkillDamageKind.Pulse, at * pulsing.HolsteredEvery); }
                skills.Schedule(index, pulsing, 0, 0, [.. pulses.Where(pulse => pulse.Time < options.Duration)]);
            }
            var fightKills = 0;
            var fightKillSeconds = 0d;
            var timedKills = 0;
            var skillHitGroups = new HashSet<int>();
            var externalKillPending = false;
            double weaponTotal = 0, skillTotal = 0;
            double skillImpactTotal = 0, skillContinuousTotal = 0;
            var buffs = options.DriveSimpleBuffs ? new TimedBuffState(fightBuffRules, magazines, stats) : null;
            // Chances draw from their own stream, so a rule with one leaves every other roll as it was.
            if (buffs is not null) { buffs.Roll = new SimulationRandom(options.Seed, (1UL << 61) | (uint)fight).NextDouble; }
            IReadOnlySet<string> fightTalents = fightBuffRules.Select(rule => rule.Talent).ToHashSet(StringComparer.Ordinal);
            // Selected talents, and the per-weapon copies of one on both rotated weapons.
            IReadOnlySet<string> everyTalent = selected.Concat(fightTalents).ToHashSet(StringComparer.Ordinal);
            var statuses = options.DriveSimpleBuffs ? new StatusTriggerState(pack, statusRules) : null;
            // A skill's own burn or bleed keeps its rules per enemy in any fight, driven or not.
            var skillStatuses = statuses ?? (options.IncludeSkills ? new StatusTriggerState(pack, []) : null);
            var dotsUsed = !dotRules.IsEmpty || !talentDamageRules.IsEmpty;
            StatusDamageState? NewDots() => dotsUsed ? new(dotRules, talentDamageRules) : null;
            var statusTicks = initialTicks;
            double statusTotal = 0;
            double talentTotal = 0;
            var lastState = options.DriveSimpleBuffs ? StateKey(context) : "";
            var lastChanged = context;
            // Other enemies' fire in the current state, by profile and distance.
            var shooters = new Dictionary<(int?, double?), EnemyFireSchedule>();
            var falloffDeclared = WeaponFalloff.Declared(resolved);
            var buffWeapon = initialWeapon;
            var enemyProfile = perTargetFire ? sequenceFire[sequence[0]] : incoming;
            var player = incoming?.CreateState();
            var playerStatuses = reportsPlayerStatuses ? new PlayerStatusState() : null;
            // Rules gated on a status on the player read it here; without statuses on the player they never gain.
            if (buffs is not null && playerStatuses is not null) { buffs.PlayerStatusOn = playerStatuses.IsOn; }
            var shield = shieldProfile?.CreateState();
            // Set by a break while the shield bars a weapon; the build's weapon returns at the next decision point.
            var shieldReturn = false;
            var recoveryProfile = initialRecovery;
            // Independent armor repairs over time: armor_repair grants with a duration (Preservation, R41).
            var grantRepairs = new TimedArmorRepairQueue();
            var recoveryTime = 0d;
            // ADR 0037: the player down with a revive on its way, and until when a revived one is immune.
            DownedPlayer? down = null;
            var immuneUntil = double.NegativeInfinity;
            var queuedRepairs = new TimedArmorRepairQueue();
            // Collection times of dropped pickups (Kill Confirmed's trophies), in order; a separate stream keeps other rolls fixed.
            var pickups = new Queue<double>();
            var pickupRandom = new SimulationRandom(options.Seed, (1UL << 62) | (uint)fight);
            double pendingRegeneration = 0, pendingRegenerationAt = 0, pendingRate = 0;
            double pendingShieldRepair = 0, pendingShieldAt = 0, pendingShieldRate = 0;
            // Disjoint fight stream ids preserve all outgoing weapon rolls.
            var enemyRandom = new SimulationRandom(options.Seed, (1UL << 63) | (uint)fight);
            var enemyIndex = 0;
            // Each target's own fire, by its place in the fight's order.
            EnemyFireSchedule FireOf(int place) => sequence.IsEmpty ? incoming! : sequenceFire[sequence[place % sequence.Length]];
            // With a sequence or several enemies, each enemy has its own clock; otherwise one schedule serves every fight.
            var group = perTargetFire || options.EnemiesAtOnce is not null ? new EnemyGroup(options.EnemiesAtOnce ?? 1, place => FireOf(place).Interval, enemyTotal) : null;
            var nextEnemy = group?.Next ?? (enemyShotTimes.IsEmpty ? double.PositiveInfinity : enemyShotTimes[0]);
            var reloadStarting = false;
            // A reload a down cut short starts over with rounds left too, as a top-up in cover did (ADR 0037).
            var reloadRestarting = false;
            var reloadCompleting = false;
            // With a cover cycle: the earliest the next shot may fire (the fire interval outlasts a short cover phase), and the
            // last cycle whose cover phase has had its reload decision.
            var shotReady = 0d;
            var coveredCycle = -1d;
            var kits = armorKit is null || kitAbility is not null ? null : new ArmorKitState(armorKit, options.ArmorKits!.Value, options.ArmorKitAtPercent!.Value);
            // With a kit ability (ADR 0021): when it's next ready, and the instant kits left and when the next is ready.
            double abilityReady = 0, instantReady = 0;
            var headshots = cycleRow is null ? null : new HeadshotCycleState(cycleRow, cycleAllowance);
            var nextHit = bonusCaps is null ? null : new NextHitBonusState(bonusRow!.Share, bonusRow.Window);
            var instantLeft = kitAbility is null ? 0 : options.ArmorKits!.Value;
            var random = new SimulationRandom(options.Seed, (ulong)fight);
            // A redirect's receiver is drawn from its own stream, so a fight's shots draw as they would without one.
            var redirectRandom = new SimulationRandom(options.Seed, (1UL << 60) | (uint)fight);
            // ADR 0029: outright kills roll on their own stream, and land their delay later.
            var killRandom = new SimulationRandom(options.Seed, (1UL << 59) | (uint)fight);
            var deaths = new List<(double At, FightEnemy Enemy, string Slot)>();
            // ADR 0036: each plan's last mark, by plan index.
            var zoneMarks = new Dictionary<int, ZoneMark>();
            double total = 0, time = 0, ammo = initialWeapon.Magazine;
            var magazine = options.DriveSimpleBuffs && template is null ? phases!.TrackMagazine(initialWeapon.Magazine) : null;
            var counts = enemyCounts?.Fresh();
            var heldMagazine = options.DriveSimpleBuffs && template is not null && !magazineRules.IsEmpty ? new HeldMagazinePhaseState(magazineRules) : null;
            // The magazine as last filled (ADR 0025): a state's magazine applies at the next fill, and magazine phases read this.
            var filled = initialWeapon.Magazine;
            void SetAmmo(double value, double at)
            {
                magazine?.Observe(value, Math.Min(at, options.Duration));
                ammo = value;
            }
            // A fill loads the magazine of the state as it stands now, read at the fill: a buff ending during a reload, or turned on by
            // the event that asked for a refill, counts.
            void Fill(double at)
            {
                RefreshWeapon(at);
                filled = buffWeapon.Magazine;
                magazine?.Observe(filled, Math.Min(at, options.Duration), filled);
                ammo = filled;
            }
            var rotation = template?.Fresh();
            double ArmorFactor(string stat) => stat.Length == 0 ? 1 : 1 + (heldTicks is null ? statusTicks : heldTicks[rotation!.ActiveSlot])[stat];
            // The weapon in hand changes when a swap starts; the rotation's active slot changes when the swap is scheduled.
            var hand = active;
            // Holstered-gain rules (Quick Draw) start gaining when the fight starts without one of their weapons in hand.
            buffs?.Hold(0, weaponTags[hand]);
            // Primed rules (ADR 0019) start their time for the weapon that starts away.
            buffs?.Prime(0, holdingTalents[hand]);
            var lastWeaponKills = new Dictionary<string, double>(StringComparer.Ordinal);
            // Swap and reload starts of scheduled rotation actions, fired in time order with the fight's other events.
            // Swap starts, reload starts and, when a rule reads it, reload completions, in time order.
            var actionEvents = new Queue<(double At, WeaponAction Action, bool Swap, bool End)>();
            // Whether a refill earned since the last one was taken reaches both rotated weapons (refill "both", ADR 0019).
            var refillBoth = false;
            // Cooldown grants held while no cooldown ran (Skilled's banked reset), each distinct one once, with the first cooldown start
            // seen since it was held. The earliest is kept, as a slot's next use replaces what it knows of the last.
            var holds = new List<(double Share, double Seconds, double Delay, double Since, double Start)>();
            // The next whole second of the fight, when a selected rule counts seconds (Stand Your Ground, Chemical Agent).
            var nextSecond = (secondsUsed || drainUsed) && 1 < options.Duration ? 1d : double.PositiveInfinity;
            // Targets cycle through the sequence and the distances; each carries its own full pools and distance.
            var targetCount = 0;
            // A dead target stays until the next arrives, so this tells a kill still being dealt with from the wait after it.
            var groupMovedOn = false;
            var arrival = 0d;
            var available = 0d;
            // Enemies the player has shot and left, by place, and who comes next: with a shooting pattern, the next in turn.
            var left = new Dictionary<int, FightEnemy>();
            // Enemies a blast killed while the player was elsewhere, settled once the event that set it off is done.
            var blastKills = new Queue<(FightEnemy Enemy, double At)>();
            // Under focus fire with blasts: nobody is left to arrive after the enemy being shot.
            var nobodyComing = false;
            // When each spread may next fire.
            var spreadReady = new double[spreadRules.Length];
            // Each trap's tags, oldest first, and when the trap is ready again; and the enemy the player has been hitting without a
            // break, with the weapon in hand, since when.
            var trapTags = trapRules.Select(_ => new List<FightEnemy>()).ToArray();
            var trapReady = new double[trapRules.Length];
            FightEnemy? aimed = null;
            var aimedSince = 0d;
            string? aimedWith = null;
            var comingPlace = 0;
            var moveAt = double.PositiveInfinity;
            // The player's state reads and writes what this enemy holds.
            void HoldState(FightEnemy enemy, double at)
            {
                skillStatuses?.Hold(enemy.Statuses, at);
                buffs?.HoldEnemy(enemy.Marks, at);
            }
            FightEnemy NewTarget()
            {
                groupMovedOn = false;
                if (left.Remove(comingPlace, out var enemy)) { enemy.Reach(available); }
                else
                {
                    targetCount++;
                    var profile = sequence.IsEmpty ? -1 : sequence[comingPlace % sequence.Length];
                    var (fullArmor, fullHealth) = profile < 0 ? (armor, health) : sequencePools[profile];
                    enemy = new(comingPlace, profile, distances.IsEmpty ? options.Distance : distances[comingPlace % distances.Length], fullArmor, fullHealth, available,
                        incoming?.Attackers ?? 1, profiles.Keys, NewDots());
                }
                HoldState(enemy, available);
                if (options.FocusSeconds is { } focus) { moveAt = available + focus; }
                // With blasts, a waiting enemy can die out of order, so the next arrival is the earliest one left.
                else if (reachActive)
                {
                    var next = group!.NextArrival(enemy.Place);
                    (comingPlace, nobodyComing) = (next ?? enemy.Place, next is null);
                }
                else { comingPlace = targetCount; }
                return enemy;
            }
            var target = NewTarget();
            // Held once: state refreshes and events read it all fight.
            Func<SimTriggerDefinition, double, int> present = (rule, at) => CountPresent(rule.Status.Length == 0 ? group!.Alive :
                group!.Alive.Where(place => EnemyAt(place)?.Statuses.Has(rule.Status, at) == true), rule.Within);
            // A driven state reads the current target's profile; between a kill and the next arrival, the arriving one's.
            // After the last enemy no target arrives, so readings stay on it.
            var noTargetsLeft = false;
            int TargetInPlay() => target.Dead && !noTargetsLeft ? sequence[comingPlace % sequence.Length] : target.Profile;
            ImmutableDictionary<string, string> OnTarget(ImmutableDictionary<string, string> read)
            {
                if (target.Profile >= 0) { read = read.SetItem(TargetProfileKey, TargetInPlay().ToString(CultureInfo.InvariantCulture)); }
                read = counts?.Context(read, present) ?? read;
                return distanceBands.Count == 0 ? read : AtDistance(read, DistanceNow()!.Value);
            }
            void AdvanceEnemy(double at)
            {
                if (group is null) { nextEnemy = ++enemyIndex < enemyShotTimes.Length ? enemyShotTimes[enemyIndex] : double.PositiveInfinity; return; }
                group.Advance(at);
                nextEnemy = group.Next;
            }
            // The current target's distance; between a kill and the next arrival, the arriving one's.
            double? DistanceNow() => distances.IsEmpty ? options.Distance : target.Dead && !noTargetsLeft ? distances[comingPlace % distances.Length] : target.Distance;
            // With target distances, a state also depends on the distance when the pack has falloff, so its key carries it then
            // (distance rules already put their keys in the state).
            string StateAt(ImmutableDictionary<string, string> changed, double? distance) => distances.IsEmpty || !falloffDeclared ? StateKey(changed) :
                StateKey(changed) + "\u001fdistance=" + distance?.ToString("R", CultureInfo.InvariantCulture);
            void Record(DummyEventKind kind, double damage = 0, double? at = null, string? slot = null)
            {
                if (damage != 0 && !kind.CarriesAmount()) { throw new InvalidOperationException($"Event {kind} does not declare an amount"); }
                if (!options.CaptureFirstFightEvents || fight != 0) { return; }
                if (events.Count == TraceLimit) { truncated = true; return; }
                // Continuous recovery includes the final interval ending at the horizon.
                if ((at ?? time) < options.Duration || kind is DummyEventKind.ArmorRepaired or DummyEventKind.ShieldRepaired && at == options.Duration)
                { events.Add(new(at ?? time, kind, damage, slot ?? (rotation?.ActiveSlot ?? ""))); }
            }
            if (shield is not null) { shieldDeployments++; Record(DummyEventKind.ShieldRaised, at: 0, slot: shieldProfile!.Slot); }
            void FlushShieldRepair()
            {
                if (pendingShieldRepair > 0) { Record(DummyEventKind.ShieldRepaired, pendingShieldRepair, pendingShieldAt, shieldProfile!.Slot); }
                pendingShieldRepair = 0;
            }
            // A break, by an enemy's hit or a talent's drain: its cooldown has started, and talents hear it.
            void ShieldBroken(double at)
            {
                shieldBreaks++;
                shieldReturn = shieldWeapon is not null;
                Record(DummyEventKind.ShieldBreak, at: at, slot: shieldProfile!.Slot);
                if (buffs is not null)
                {
                    FireStatuses("shield_break", at, buffWeapon.StatusDurationFactor);
                    var shieldRefill = FireBuffs("shield_break", at, statuses!.Active(at));
                    if (shieldRefill) { Refill(at); }
                    RefreshWeapon(at);
                }
            }
            void AdvanceShield(double at, double queuedRate)
            {
                if (shield is null) { return; }
                var recovery = shield.Advance(at, queuedRate);
                var rate = shieldProfile!.Regeneration + queuedRate;
                if (recovery.Repaired > 0)
                {
                    shieldRepairs[fight] += recovery.Repaired;
                    if (!double.IsFinite(shieldRepairs[fight])) { throw new SimulationException("repair-overflow", "Shield recovery totals overflowed"); }
                    if (rate != pendingShieldRate) { FlushShieldRepair(); }
                    pendingShieldRepair += recovery.Repaired;
                    pendingShieldAt = at;
                    pendingShieldRate = rate;
                    if (shield.Full) { FlushShieldRepair(); }
                }
                if (recovery.RaisedAt is { } raised && raised < options.Duration)
                { shieldDeployments++; Record(DummyEventKind.ShieldRaised, at: raised, slot: shieldProfile.Slot); }
            }
            void AddRepair(double armorAmount, double healthAmount)
            {
                armorRepairs[fight] += armorAmount;
                healthRepairs[fight] += healthAmount;
                if (!double.IsFinite(armorRepairs[fight]) || !double.IsFinite(healthRepairs[fight]))
                { throw new SimulationException("repair-overflow", "Repair totals overflowed"); }
            }
            // One trace entry covers a run of regeneration at one rate, ending when the rate changes, armor
            // fills, it stops, or a discrete pool change follows; per-step entries would flood the trace.
            void FlushRegeneration()
            {
                if (pendingRegeneration > 0) { Record(DummyEventKind.ArmorRepaired, pendingRegeneration, pendingRegenerationAt, ""); }
                pendingRegeneration = 0;
            }
            // A repair skill's armor repair is its own event, with its slot (ADR 0030).
            void RecordRepair(double armorAmount, double healthAmount, double at, string skillSlot = "")
            {
                FlushRegeneration();
                AddRepair(armorAmount, healthAmount);
                if (armorAmount > 0) { Record(skillSlot.Length > 0 ? DummyEventKind.SkillRepair : DummyEventKind.ArmorRepaired, armorAmount, at, skillSlot); }
                if (healthAmount > 0) { Record(DummyEventKind.HealthRepaired, healthAmount, at, ""); }
            }
            // Every running skill cooldown, the shield's included: a share of each one's own (a reset at 1), or seconds.
            void CutCooldowns(double at, double share, double seconds)
            {
                skills?.Shorten(at, share, seconds);
                if (shield is not null)
                {
                    AdvanceShield(at, queuedRepairs.Rate);
                    shield.Shorten(share, seconds, at);
                    AdvanceShield(at, queuedRepairs.Rate);
                }
            }
            // The first cooldown start at or after a time that the fight knows of now: a skill's, or the shield's break.
            double StartSince(double since)
            {
                var start = skills?.CooldownStartSince(since) ?? double.PositiveInfinity;
                return shield?.BrokenAt >= since ? Math.Min(start, shield.BrokenAt) : start;
            }
            // When the next held grant lands: its delay after the first cooldown start since it was held. Called every pass of the fight's
            // loop, so a start is recorded before a later use of its slot replaces it.
            double HeldDue()
            {
                var due = double.PositiveInfinity;
                for (var index = 0; index < holds.Count; index++)
                {
                    var hold = holds[index];
                    hold.Start = Math.Min(hold.Start, StartSince(hold.Since));
                    holds[index] = hold;
                    due = Math.Min(due, hold.Start + hold.Delay);
                }
                return due;
            }
            // Clearing stacks can grant temporary armor or repair the shield (Full Stop, Liberty or Death).
            void ApplyGrants(double at)
            {
                foreach (var grant in buffs!.TakeGrants())
                {
                    if (at >= options.Duration) { continue; }
                    if (grant.Kind == "temporary_armor" && player is not null)
                    {
                        var amount = player.GrantBonusArmor(grant.Share * player.MaxArmor, at, at + grant.Duration);
                        // Like a repair, a grant closes the running regeneration trace entry first.
                        if (amount > 0) { FlushRegeneration(); Record(DummyEventKind.BonusArmorGranted, amount, at, ""); }
                    }
                    else if (grant.Kind is "armor_repair" or "missing_armor_repair" && player is not null && !player.Downed)
                    {
                        // A share of max armor, or of the armor missing now (Clutch). Spread over a duration it joins the independent
                        // repairs Preservation uses (R41); the event's handler has already walked recovery to this time.
                        var amount = grant.Share * (grant.Kind == "armor_repair" ? player.MaxArmor : player.MaxArmor - player.Armor);
                        if (grant.Duration > 0) { grantRepairs.Enqueue(amount, grant.Duration, at); }
                        else
                        {
                            var repair = player.Repair(amount, 0, at);
                            RecordRepair(repair.Armor, 0, at);
                        }
                    }
                    else if (grant.Kind is "skill_cooldown" or "skill_cooldown_seconds")
                    {
                        var (share, seconds) = grant.Kind == "skill_cooldown" ? (grant.Share, 0d) : (0d, grant.Share);
                        // With no cooldown running a holding grant waits for the next to start (Skilled's banked reset), one at a time.
                        if (grant.HoldDelay > 0 && skills?.CooldownRunning(at) != true && shield?.CoolingDown(at) != true)
                        {
                            if (!holds.Any(hold => hold.Share == share && hold.Seconds == seconds && hold.Delay == grant.HoldDelay))
                            { holds.Add((share, seconds, grant.HoldDelay, at, StartSince(at))); }
                            continue;
                        }
                        CutCooldowns(at, share, seconds);
                    }
                    else if (grant.Kind == "armor_kit_refund" && kitAbility is not null)
                    {
                        // With a kit ability the kit spent is an instant kit, and it comes back the same way, never above those carried.
                        if (instantLeft < options.ArmorKits!.Value) { instantLeft++; }
                    }
                    else if (grant.Kind == "armor_kit_refund" && kits is not null)
                    {
                        // On the armor_kit event the kit just spent comes back, so that use cost none (Efficient).
                        kits.Refund();
                    }
                    else if (grant.Kind == "shield_repair" && shield is not null)
                    {
                        // Advance first: a shield whose cooldown has ended is raised before the repair.
                        AdvanceShield(at, queuedRepairs.Rate);
                        if (!shield.IsUp) { continue; }
                        FlushShieldRepair();
                        var repaired = shield.Repair(grant.Share * shield.Capacity, at);
                        shieldRepairs[fight] += repaired;
                        if (!double.IsFinite(shieldRepairs[fight])) { throw new SimulationException("repair-overflow", "Shield recovery totals overflowed"); }
                        if (repaired > 0) { Record(DummyEventKind.ShieldRepaired, repaired, at, shieldProfile!.Slot); }
                    }
                }
            }
            // Without rotation every selected talent reacts; under rotation only gear and the weapon in hand do.
            bool FireStatuses(string name, double at, double factor, IReadOnlySet<string>? eligible = null, TriggerGates? gates = null) =>
                Spread(statuses!.Fire(name, at, factor, eligible ?? holdingTalents[hand], gates: gates ?? (gated ? buffs!.Gates(at) : null), distance: target.Distance), at,
                    eligible ?? holdingTalents[hand]);
            // weapon is the slot whose weapon produced the event; null for events with no weapon behind them.
            bool FireBuffs(string name, double at, IReadOnlySet<string> active, IReadOnlySet<string>? eligible = null,
                IReadOnlySet<string>? owners = null, string? weapon = null)
            {
                var requested = buffs!.Fire(name, at, active, eligible ?? holdingTalents[hand], owners ?? holdingTalents[hand],
                    weapon is null ? null : weaponTags[weapon], player is null || player.MaxArmor <= 0 ? null : player.Armor / player.MaxArmor,
                    buffs.ReadsStats(name) ? StatsBefore(weapon ?? hand, at) : null, weapon is null ? null : otherWeaponTags[weapon], target.Distance);
                refillBoth |= requested && buffs.RefillsBoth;
                counts?.Sample(name, eligible ?? holdingTalents[hand], targeted && target.Dead && !groupMovedOn, at, present);
                // Every event passes here, so a talent debuff (Toxic Delivery) starts on its event on a living target.
                target.Dots?.StartTalents(name, at, eligible ?? holdingTalents[hand], statusTicks, !targeted || !target.Dead);
                Blast(name, at, eligible ?? holdingTalents[hand]);
                Trap(name, at, eligible ?? holdingTalents[hand]);
                ApplyGrants(at);
                // Every event is a decision a held skill waits for (ADR 0014).
                skills?.Release(at);
                return requested;
            }
            // Weapon repair-per-kill follows weapon and skill kills only. A marked target drops Doctor Home's kit however it
            // dies, a burn or bleed kill included (R41).
            void RepairOnKill(double at, bool weaponBound = true)
            {
                if (player is null || at >= options.Duration) { return; }
                var amount = weaponBound ? recoveryProfile!.KillArmor : Math.Max(0, recoveryProfile!.KillArmor - recoveryProfile.WeaponKillArmor);
                var repair = player.Repair(amount, recoveryProfile.KillHealth, at);
                RecordRepair(repair.Armor, repair.Health, at);
            }
            // The bookkeeping every kill shares; repairs differ by what killed the target. A null slot is the active weapon. A kill of an
            // enemy the player has left doesn't interrupt them, unless it's the one they were moving to.
            void CreditKill(double at, string? slot, Action repairs, bool away = false)
            {
                kills++;
                // Kills before the fight's end count, as its repairs and pickups do.
                if (at < options.Duration)
                {
                    // An enemy a blast killed before the player reached it has no time to kill (ADR 0013).
                    if (target.Reached)
                    {
                        fightKillSeconds += at - target.ArrivedAt;
                        timedKills++;
                    }
                    if (++fightKills == options.ClearKills) { clearTimes.Add(at); }
                }
                if (slot is null) { lastWeaponKills[hand] = at; }
                if (fight == 0 && killTimes.Count < TraceLimit && target.Reached) { killTimes.Add(at - target.ArrivedAt); }
                else if (fight == 0) { truncated = true; }
                Record(DummyEventKind.Kill, at: at, slot: slot);
                // Every kill drops a pickup when a rule collects them: picked up after the delay, or missed.
                if (pickupsUsed && at < options.Duration && pickupRandom.NextDouble() < options.PickupChance) { pickups.Enqueue(at + options.PickupDelay); }
                repairs();
                var moving = options.FocusSeconds is null ? null : group!.Following(target.Place);
                if (away && comingPlace != target.Place) { return; }
                // After the last enemy no target ever arrives, so skills stop and the fight runs out its length.
                noTargetsLeft = options.FocusSeconds is null ? reachActive ? group!.NextArrival(target.Place) is null : targetCount >= enemyTotal : moving is null;
                if (moving is { } next) { comingPlace = next; }
                if (away && !noTargetsLeft) { return; }
                // The arrival a move recorded won't come now.
                for (var index = events.Count - 1; away && fight == 0 && index >= 0; index--)
                { if (events[index].Kind == DummyEventKind.TargetArrived && events[index].Time > at) { events.RemoveAt(index); } }
                available = noTargetsLeft ? double.PositiveInfinity : at + retargetDelay;
                if (available < options.Duration) { Record(DummyEventKind.TargetArrived, at: available, slot: slot is null ? null : ""); }
            }
            // An event of the player's own, with no weapon behind it. As with other such events, gear and the weapon in hand react to it,
            // but what it clears clears on every talent, holstered weapons and their copies too. A status rule arms for the next hit.
            void FirePlayerEvent(string name, double at)
            {
                FireStatuses(name, at, buffWeapon.StatusDurationFactor);
                if (FireBuffs(name, at, statuses!.Active(at), owners: everyTalent)) { Refill(at); }
            }
            // The player leaves a living enemy, which keeps what's on it, and sets off for the next (ADR 0011).
            void MoveOn(double at)
            {
                // The event comes while the player is still on this enemy, so a rule it clears clears there. It reaches every talent in
                // the fight: a holstered weapon's stacks on this enemy don't carry to the next.
                FirePlayerEvent("target_changed", at);
                comingPlace = group!.Following(target.Place)!.Value;
                left[target.Place] = target;
                target = FightEnemy.Between(target);
                groupMovedOn = true;
                moveAt = double.PositiveInfinity;
                HoldState(target, at);
                available = at + retargetDelay;
                if (available < options.Duration) { Record(DummyEventKind.TargetArrived, at: available, slot: ""); }
                RefreshWeapon(at);
            }
            // Whoever's at this place and has been reached: one left behind, or the one being shot. During a move the stand-in
            // shares the old enemy's place, so those left come first.
            FightEnemy? EnemyAt(int place) => left.GetValueOrDefault(place) ?? (place == target.Place ? target : null);
            // The enemy with the next tick: the one being shot, or one left behind.
            FightEnemy? Ticking()
            {
                var soonest = target.Dots?.NextTick is null ? null : target;
                foreach (var enemy in left.Values)
                { if (enemy.Dots?.NextTick < (soonest?.Dots!.NextTick ?? double.PositiveInfinity)) { soonest = enemy; } }
                return soonest;
            }
            // Returns whether the walk refreshed the weapon state at the requested time.
            bool Regenerate(double at)
            {
                // While the player is down the shield and what's queued run on, with nothing restored (ADR 0037).
                if (down is not null)
                {
                    AdvanceShield(at, 0);
                    queuedRepairs.Advance(at);
                    grantRepairs.Advance(at);
                    recoveryTime = at;
                    return false;
                }
                if (player is null || player.Downed) { return false; }
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Full armor has nothing to integrate, so its state boundaries need no walk.
                    if (player.ArmorFull && (shield is null || shield.Full || !shield.IsUp) && (shield is null || buffs is null) && (skillSchedule is null || buffs is null))
                    {
                        FlushRegeneration();
                        player.Regenerate(0, at);
                        AdvanceShield(at, 0);
                        queuedRepairs.Advance(at);
                        grantRepairs.Advance(at);
                        recoveryTime = at;
                        return false;
                    }
                    var boundary = Math.Min(at, Math.Min(buffs?.NextExpiry ?? double.PositiveInfinity,
                        Math.Min(statuses?.NextExpiry ?? double.PositiveInfinity, phases?.NextTransition(recoveryTime) ?? double.PositiveInfinity)));
                    boundary = Math.Min(boundary, Math.Min(queuedRepairs.NextExpiry ?? double.PositiveInfinity, grantRepairs.NextExpiry ?? double.PositiveInfinity));
                    if (zoneKey.Length > 0 && NextMarkEnd(recoveryTime) is var markEnd && markEnd < boundary) { boundary = markEnd; }
                    if (target.Dead && available > recoveryTime) { boundary = Math.Min(boundary, available); }
                    var rate = recoveryProfile!.ArmorPerSecond + queuedRepairs.Rate + grantRepairs.Rate;
                    if (!double.IsFinite(rate)) { throw new SimulationException("repair-overflow", "Combined continuous armor repair rate overflowed"); }
                    var repaired = player.Regenerate(rate, boundary);
                    AdvanceShield(boundary, queuedRepairs.Rate);
                    queuedRepairs.Advance(boundary);
                    grantRepairs.Advance(boundary);
                    AddRepair(repaired.Armor, 0);
                    if (repaired.Armor > 0)
                    {
                        if (rate != pendingRate) { FlushRegeneration(); }
                        pendingRegeneration += repaired.Armor;
                        pendingRegenerationAt = boundary;
                        pendingRate = rate;
                        if (player.ArmorFull) { FlushRegeneration(); }
                    }
                    else if (boundary > recoveryTime) { FlushRegeneration(); }
                    recoveryTime = boundary;
                    // The endpoint closes recovery; new discrete state at the horizon is outside the fight.
                    if (boundary == options.Duration) { return false; }
                    if (target.Dead && boundary >= available) { target = NewTarget(); arrival = available; }
                    RefreshWeapon(boundary);
                    if (boundary == at) { return true; }
                    Spend();
                }
            }
            WeaponAction? lastAction = null;
            void ScheduleAction(WeaponAction action)
            {
                lastAction = action;
                if (action.Kind == WeaponActionKind.Swap)
                {
                    if (action.StartedAt < options.Duration) { swaps++; }
                    Record(DummyEventKind.SwapStarted, at: action.StartedAt, slot: action.From);
                    Record(DummyEventKind.SwapCompleted, at: action.SwapCompletedAt, slot: action.To);
                    if (buffs is not null) { actionEvents.Enqueue((action.StartedAt, action, true, false)); }
                }
                if (action.ReloadStartsAt is { } begins)
                {
                    if (begins < options.Duration) { reloads++; }
                    if (buffs is not null) { actionEvents.Enqueue((begins, action, false, false)); }
                    if (buffs is not null && reloadEndUsed) { actionEvents.Enqueue((action.ReadyAt, action, false, true)); }
                    Record(DummyEventKind.ReloadStarted, at: begins, slot: action.To);
                    Record(DummyEventKind.ReloadCompleted, at: action.ReadyAt, slot: action.To);
                }
                time = action.ReadyAt;
            }
            // Undoes all ScheduleAction did for an action that hasn't begun, or for the reload left of a swap that has.
            void CancelAction(WeaponAction action)
            {
                var kept = actionEvents.Where(entry => !ReferenceEquals(entry.Action, action)).ToArray();
                actionEvents.Clear();
                foreach (var entry in kept) { actionEvents.Enqueue(entry); }
                var swapBegun = action.Kind == WeaponActionKind.Swap && hand == action.To;
                if (action.Kind == WeaponActionKind.Swap && !swapBegun)
                {
                    if (action.StartedAt < options.Duration) { swaps--; }
                    Unrecord(DummyEventKind.SwapStarted, action.StartedAt, action.From);
                    Unrecord(DummyEventKind.SwapCompleted, action.SwapCompletedAt!.Value, action.To);
                }
                if (action.ReloadStartsAt is { } begins)
                {
                    if (begins < options.Duration) { reloads--; }
                    Unrecord(DummyEventKind.ReloadStarted, begins, action.To);
                    Unrecord(DummyEventKind.ReloadCompleted, action.ReadyAt, action.To);
                }
                time = rotation!.Cancel(action, swapBegun);
                if (ReferenceEquals(lastAction, action)) { lastAction = null; }
            }
            // A trace already cut short stays as it is, so it can't reopen with a gap in it.
            bool Unrecord(DummyEventKind kind, double at, string slot)
            {
                if (!options.CaptureFirstFightEvents || fight != 0 || truncated) { return false; }
                for (var index = events.Count - 1; index >= 0; index--)
                {
                    if (events[index].Kind == kind && events[index].Time == at && events[index].WeaponSlot == slot) { events.RemoveAt(index); return true; }
                }
                return false;
            }
            // ADR 0037: the player goes down at a time with a weapon action under way or ahead, so it waits until the revive: a reload begun
            // starts over then, and a swap begun finishes, its reload on arrival waiting.
            void DeferAction(WeaponAction action, double at, double until)
            {
                var swapBegun = action.Kind == WeaponActionKind.Swap && hand == action.To;
                var put = rotation!.Defer(action, swapBegun, until);
                if (ReferenceEquals(put, action)) { return; }
                var kept = actionEvents.Where(entry => !ReferenceEquals(entry.Action, action)).ToArray();
                actionEvents.Clear();
                foreach (var entry in kept) { actionEvents.Enqueue(entry); }
                void Move(DummyEventKind kind, double was, double now, string slot)
                { if (Unrecord(kind, was, slot) || was >= options.Duration && !truncated) { Record(kind, at: now, slot: slot); } }
                if (action.Kind == WeaponActionKind.Swap && !swapBegun)
                {
                    if (action.StartedAt < options.Duration && put.StartedAt >= options.Duration) { swaps--; }
                    Move(DummyEventKind.SwapStarted, action.StartedAt, put.StartedAt, action.From);
                    Move(DummyEventKind.SwapCompleted, action.SwapCompletedAt!.Value, put.SwapCompletedAt!.Value, action.To);
                    actionEvents.Enqueue((put.StartedAt, put, true, false));
                }
                if (action.ReloadStartsAt is { } was && put.ReloadStartsAt is { } now)
                {
                    // One begun is cut short and starts over, a reload of its own; one that hadn't just starts later.
                    if (was < at)
                    {
                        if (now < options.Duration) { reloads++; }
                        Record(DummyEventKind.ReloadStarted, at: now, slot: action.To);
                    }
                    else
                    {
                        if (was < options.Duration && now >= options.Duration) { reloads--; }
                        Move(DummyEventKind.ReloadStarted, was, now, action.To);
                    }
                    Move(DummyEventKind.ReloadCompleted, action.ReadyAt, put.ReadyAt, action.To);
                    actionEvents.Enqueue((now, put, false, false));
                    if (reloadEndUsed) { actionEvents.Enqueue((put.ReadyAt, put, false, true)); }
                }
                lastAction = put;
                time = put.ReadyAt;
            }
            // A queued action timed again (a swap's own time, or its reload on arrival): its queue entries, its arrival and reload in the trace,
            // and the fight's next decision follow, which never comes before the weapon is ready.
            void ReplaceAction(WeaponAction old, WeaponAction updated)
            {
                var kept = actionEvents.Select(entry => ReferenceEquals(entry.Action, old)
                    ? (entry.Swap ? entry.At : entry.End ? updated.ReadyAt : updated.ReloadStartsAt ?? entry.At, updated, entry.Swap, entry.End) : entry).ToArray();
                actionEvents.Clear();
                foreach (var entry in kept) { actionEvents.Enqueue(entry); }
                if (ReferenceEquals(lastAction, old)) { lastAction = updated; }
                // An event the trace had at its old time moves; one past the fight's end wasn't recorded, so it's recorded if it now falls inside.
                void Move(DummyEventKind kind, double? before, double? after, string slot)
                {
                    if (before is not { } was || after is not { } now || was == now) { return; }
                    if (Unrecord(kind, was, slot) || was >= options.Duration && !truncated) { Record(kind, at: now, slot: slot); }
                }
                Move(DummyEventKind.SwapCompleted, old.SwapCompletedAt, updated.SwapCompletedAt, old.To);
                Move(DummyEventKind.ReloadStarted, old.ReloadStartsAt, updated.ReloadStartsAt, old.To);
                Move(DummyEventKind.ReloadCompleted, old.ReloadStartsAt is null ? null : old.ReadyAt, updated.ReloadStartsAt is null ? null : updated.ReadyAt, old.To);
                // A reload counts when it starts inside the fight, so one moved across the fight's end changes the count.
                if (old.ReloadStartsAt is { } wasStart && updated.ReloadStartsAt is { } nowStart && wasStart < options.Duration != nowStart < options.Duration)
                { reloads += nowStart < options.Duration ? 1 : -1; }
                time = time == old.ReadyAt ? updated.ReadyAt : Math.Max(time, updated.ReadyAt);
            }
            // A dead target's statuses and the pellets that landed on it never carry to the next one.
            void ClearTarget(double at, bool talents = true)
            {
                // The target's death reaches every talent in the fight, after its kill events and however it died (R41: Doctor
                // Home's kit; a mark consumed here clears with its target). An outright kill (ADR 0029) gives no talent credit: marks and
                // stacks that end with their target still clear, but no talent gains from the death, and nothing passes on.
                if (buffs is not null && at < options.Duration &&
                    FireBuffs("target_death", at, statuses!.Active(at), talents ? fightTalents : NoTalents, fightTalents)) { Refill(at); }
                foreach (var slot in profiles.Keys) { target.PelletHits[slot] = 0; }
                // Passed on while the dying enemy still holds its statuses and debuffs.
                if (talents) { PassOn(at); }
                skillStatuses?.ResetTarget(at);
                target.Dots?.Sync(statuses!, statusTicks);
                target.Dots?.ResetTalents();
                if (group is not null)
                {
                    group.Replace(target.Place, at);
                    nextEnemy = group.Next;
                    groupMovedOn = true;
                    counts?.ReadAgain(at, present);
                }
            }
            // A kill by a tick or a blast, on the enemy being shot or one the player isn't (ADR 0011, 0013): it runs as any status kill,
            // on that enemy, and the player then carries on.
            double NextDeath() => deaths.Count == 0 ? double.PositiveInfinity : deaths.Min(death => death.At);
            // An outright kill gives no talent credit (ADR 0029): no kill repair and no kill event.
            void KillElsewhere(FightEnemy dying, double at, bool talents = true)
            {
                var (shot, movedOn, away) = (target, groupMovedOn, !ReferenceEquals(dying, target));
                var parked = away && !shot.Dead;
                // Under focus fire with effects reaching others, a waiting enemy can die out of order (ADR 0013): the next arrival moves past
                // it first, so the kill doesn't take the arrival with it.
                var reordering = reachActive && options.FocusSeconds is null;
                if (away && reordering && comingPlace == dying.Place)
                {
                    var next = group!.NextArrival(shot.Place, dying.Place);
                    (comingPlace, nobodyComing) = (next ?? shot.Place, next is null);
                }
                if (away)
                {
                    // The enemy being shot waits with those left, so a count read on this kill still finds it.
                    left.Remove(dying.Place);
                    if (parked) { left[shot.Place] = shot; }
                    target = dying;
                    groupMovedOn = false;
                    HoldState(target, at);
                }
                // Preservation and weapon repair-per-kill are not granted: whether a status kill credits them is an in-game check
                // (R41). Doctor Home's kit is, when the target was marked.
                CreditKill(at, "", talents ? () => RepairOnKill(at, weaponBound: false) : () => { }, away);
                if (!away && options.Rotation?.Mode == WeaponSwapMode.AfterKill) { externalKillPending = true; }
                // A status kill is a kill to gear talents only, as a skill's is. It's no skill_kill: by the source a burn or bleed
                // tick isn't a skill kill (R77), though a skill's own status ticks in its stream count as one, a known partial.
                if (talents)
                {
                    FireStatuses("kill", at, buffWeapon.StatusDurationFactor, gearTalents);
                    if (FireBuffs("kill", at, statuses!.Active(at), gearTalents)) { Refill(at); }
                }
                ClearTarget(at, talents);
                if (away)
                {
                    // A blast this kill set off may have killed it meanwhile; it still comes back out, to be settled as the target.
                    if (parked) { left.Remove(shot.Place); }
                    target = shot;
                    groupMovedOn = movedOn;
                    HoldState(target, at);
                }
                // The player's own kill set an arrival before this one killed the rest: with nobody left, it won't come.
                if (away && reordering && target.Dead && !noTargetsLeft && nobodyComing)
                {
                    noTargetsLeft = true;
                    available = double.PositiveInfinity;
                    for (var index = events.Count - 1; fight == 0 && index >= 0; index--)
                    { if (events[index].Kind == DummyEventKind.TargetArrived && events[index].Time > at) { events.RemoveAt(index); } }
                }
            }
            // A blast made a waiting enemy before the player reached it: it holds its pools from now on (ADR 0013).
            FightEnemy Early(int place, double at)
            {
                var profile = sequence.IsEmpty ? -1 : sequence[place % sequence.Length];
                var (fullArmor, fullHealth) = profile < 0 ? (armor, health) : sequencePools[profile];
                var enemy = new FightEnemy(place, profile, distances.IsEmpty ? options.Distance : distances[place % distances.Length], fullArmor, fullHealth, at,
                    incoming?.Attackers ?? 1, profiles.Keys, NewDots(), reached: false);
                left[place] = enemy;
                return enemy;
            }
            // A driven value is on unless it's missing, empty, zero or false (ADR 0014's key condition).
            static bool IsOn(string? value) => value is { Length: > 0 } && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                !(double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 0);
            // A hit tags the enemy being shot once the player has hit it for the trap's time without a break, with the trap's weapon in
            // hand (The Trap). Hitting stands in for aiming, which a fight doesn't otherwise have.
            void Tag(double at)
            {
                if (!trapActive || target.Placeholder || target.Dead) { return; }
                if (!ReferenceEquals(aimed, target) || aimedWith != hand) { (aimed, aimedSince, aimedWith) = (target, at, hand); }
                for (var index = 0; index < trapRules.Length; index++)
                {
                    var rule = trapRules[index];
                    var tags = trapTags[index];
                    if (holdingTalents[hand]?.Contains(rule.Talent) == false) { continue; }
                    if (tags.Contains(target, ReferenceEqualityComparer.Instance) || at - aimedSince < rule.TagAfter - 1e-9) { continue; }
                    // The dead hold no slot, so a living tag is dropped only for another.
                    tags.RemoveAll(enemy => enemy.Dead);
                    tags.Add(target);
                    if (tags.Count > rule.MaxTags) { tags.RemoveAt(0); }
                }
            }
            // A trap whose weapon is put away while it's ready loses its tags, as in the source; one cooling down keeps them.
            void HolsterTraps(double at)
            {
                for (var index = 0; index < trapRules.Length; index++)
                {
                    if (holdingTalents[hand]?.Contains(trapRules[index].Talent) == false && at >= trapReady[index]) { trapTags[index].Clear(); }
                }
            }
            // A headshot kill springs a ready trap on every other enemy it tagged, at any distance, then starts its cooldown; during the
            // cooldown each headshot kill cuts it (The Trap).
            void Trap(string name, double at, IReadOnlySet<string>? eligible)
            {
                if (!trapActive || name != "headshot_kill" || at >= options.Duration) { return; }
                for (var index = 0; index < trapRules.Length; index++)
                {
                    var rule = trapRules[index];
                    if (eligible?.Contains(rule.Talent) == false) { continue; }
                    if (at < trapReady[index])
                    {
                        trapReady[index] = Math.Max(at, trapReady[index] - rule.Cut);
                        continue;
                    }
                    var tags = trapTags[index];
                    // The victim is dead by now, so it goes with the others that died: a kill on the only tag springs nothing.
                    tags.RemoveAll(enemy => enemy.Dead);
                    if (tags.Count == 0) { continue; }
                    var burned = 0;
                    foreach (var enemy in tags) { if (PutStatus(enemy, rule.Status, rule.StatusDuration, at, scale: true)) { burned++; } }
                    // As a spread's, the cooldown starts only when the status went on someone (one yielding to a skill's own, R63, didn't).
                    if (burned == 0) { continue; }
                    tags.Clear();
                    trapReady[index] = at + rule.Every;
                    Record(DummyEventKind.TrapSprung, burned, at, "");
                }
            }
            // A redirect's receiver (ADR 0013, Bewildered): a living enemy within reach of the one being shot, at random; null with
            // nobody in reach, or when the redirect isn't the hand's. Who is in reach is read from the group for each pellet, since a
            // pellet before it can kill.
            (FightEnemy Enemy, double Share)? Redirect(double at)
            {
                if (!reachActive || redirectRules.IsEmpty || at >= options.Duration || target.Placeholder || target.Dead || !group!.Alive.Contains(target.Place)) { return null; }
                var rule = redirectRules[0];
                if (holdingTalents[hand]?.Contains(rule.Talent) == false) { return null; }
                var places = group.InReach(target.Place, rule.Within, spacing).Where(place => EnemyAt(place)?.Dead != true).ToArray();
                if (places.Length == 0) { return null; }
                var place = places[(int)Math.Min(places.Length - 1, Math.Floor(redirectRandom.NextDouble() * places.Length))];
                return (EnemyAt(place) ?? Early(place, at), rule.Share);
            }
            // A selected talent's blast on its event: its amount to each living enemy in reach of the one the event is about, on that
            // enemy's own pools (ADR 0013). Kills it makes are settled after the event.
            void Blast(string name, double at, IReadOnlySet<string>? eligible)
            {
                if (!reachActive || at >= options.Duration || target.Placeholder || !group!.Alive.Contains(target.Place)) { return; }
                foreach (var rule in blastRules)
                {
                    if (rule.On != name || eligible?.Contains(rule.Talent) == false) { continue; }
                    var amount = rule.Amount.Length == 0 ? 0 : statusTicks[rule.Amount];
                    foreach (var (_, enemy) in LivingNear(target.Place, rule.Within, at))
                    {
                        if (rule.Amount.Length > 0)
                        {
                            var dealt = enemy.Apply(amount, 1, 1);
                            talentTotal += dealt;
                            total += dealt;
                            timeline[(int)at] += dealt / fights;
                            if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)at]))
                            { throw new SimulationException("damage-overflow", "Blast damage overflowed"); }
                            Record(DummyEventKind.TalentBlast, dealt, at, "");
                            if (enemy.Dead)
                            {
                                blastKills.Enqueue((enemy, at));
                                continue;
                            }
                        }
                        if (rule.Status.Length > 0) { PutStatus(enemy, rule.Status, rule.StatusDuration, at, scale: true); }
                        // A mark on a rule held per enemy (Unnerve), read when the player shoots that enemy.
                        // A talent on both rotated weapons keeps a copy of its rules per weapon; the copy in hand marks. Without driven
                        // rules, or with the rule's gate closed, there's nothing to mark.
                        if (rule.Mark.Length > 0 && buffs is not null &&
                            buffs.Mark(enemy.Marks, target.Marks, copiedKeys.TryGetValue(rule.Mark, out var copies) ? copies[hand] : rule.Mark, at))
                        { Record(DummyEventKind.EnemyMarked, 0, at, ""); }
                    }
                }
            }
            // The dying enemy's debuffs of a talent with a pass-on move to the nearest living enemy in reach (ADR 0013, Plague), keeping
            // their schedules, up to its stack cap; the rest, or all with nobody in reach, are lost. The event carries how many landed.
            void PassOn(double at)
            {
                if (!reachActive || passOnRules.IsEmpty || at >= options.Duration || target.Placeholder || !group!.Alive.Contains(target.Place)) { return; }
                foreach (var rule in passOnRules)
                {
                    if (rule.Status.Length > 0)
                    {
                        PassOnStatuses(rule, at);
                        continue;
                    }
                    if (target.Dots is null) { continue; }
                    var debuffs = target.Dots.TakeTalent(rule.Talent);
                    if (debuffs.Length == 0) { continue; }
                    foreach (var (_, enemy) in LivingNear(target.Place, rule.Within, at))
                    {
                        var landed = enemy.Dots!.Receive(debuffs);
                        if (landed > 0) { Record(DummyEventKind.DebuffPassedOn, landed, at, ""); }
                        break;
                    }
                }
            }
            // A status pass-on (Indirect Transmission): each named status a talent put on the dying enemy goes on the nearest living
            // enemy in reach, or all of them, for its share of the duration it went on with; where the row says so, a skill's own too
            // (ADR 0015).
            void PassOnStatuses(SimPassOnDefinition rule, double at)
            {
                // A talent's status while it runs; otherwise, where the row says so, a skill's own, for its share of one application, however
                // little was left (ADR 0015, R105). There's one of each on the dying enemy dealing damage (R63), so one of each passes on. Each
                // carries the damage it went on with: a copy's, or a skill's own; the player's rate otherwise.
                (string Status, double Duration, double? PerSecond) Passing(string status) =>
                    statuses!.Application(status) is { } on && on.Until > at ? (status, rule.Share * (on.Until - on.AppliedAt), statuses.RunningCopyRate(status))
                    : rule.SkillStatuses && SkillStatusCopies(status) && statuses.SkillApplication(status, at) is { } skill ? (status, rule.Share * skill.Span, skill.Rate)
                    : (status, 0, null);
                var passing = (rule.Status == "any" ? StatusTriggerState.Names : [rule.Status]).Select(Passing).Where(entry => entry.Duration > 0).ToArray();
                if (passing.Length == 0) { return; }
                var landed = 0;
                foreach (var (_, enemy) in LivingNear(target.Place, rule.Within, at))
                {
                    var on = false;
                    foreach (var (status, duration, perSecond) in passing) { on |= PutStatus(enemy, status, duration, at, scale: false, perSecond); }
                    if (!on) { continue; }
                    landed++;
                    // The nearest that takes it; one where each yields to a skill's own status passes it to the next in reach.
                    if (rule.To != "all") { break; }
                }
                if (landed > 0) { Record(DummyEventKind.StatusPassedOn, landed, at, ""); }
            }
            // A status goes on another enemy, read and written there, and starts its ticks; then the enemy being shot is held again,
            // whatever happens. Counts of enemies with a status read again.
            // Returns whether it went on; it yields to a skill's own status there (R63).
            bool PutStatus(FightEnemy enemy, string status, double duration, double at, bool scale, double? perSecond = null)
            {
                bool on;
                statuses!.Hold(enemy.Statuses, at);
                try
                {
                    on = statuses.ApplyDirect(status, duration, at, buffWeapon.StatusDurationFactor, scale, perSecond);
                    enemy.Dots?.Sync(statuses, statusTicks);
                }
                finally { statuses.Hold(target.Statuses, at); }
                counts?.ReadAgain(at, present);
                return on;
            }
            // Statuses newly on the enemy being shot go on each enemy in a spread's reach, for what's left of them, while it's ready
            // (Creeping Death), when its talent may react to the event. The cooldown starts when a status went on someone. Returns whether a
            // status went on the target, as given.
            bool Spread(bool applied, double at, IReadOnlySet<string>? eligible)
            {
                if (!applied || !reachActive || spreadRules.IsEmpty || at >= options.Duration || target.Placeholder || target.Dead || !group!.Alive.Contains(target.Place))
                { return applied; }
                // What's left of each status newly on the target, before any other enemy is held.
                var remaining = statuses!.NewlyOn.Select(status => (Status: status, On: statuses.Application(status)))
                    .Where(entry => entry.On is { } on && on.Until > at).Select(entry => (entry.Status, Left: entry.On!.Value.Until - at, PerSecond: (double?)null)).ToArray();
                if (remaining.Length == 0) { return applied; }
                for (var index = 0; index < spreadRules.Length; index++)
                {
                    if (eligible?.Contains(spreadRules[index].Talent) != false) { SpreadFrom(index, remaining, at); }
                }
                return applied;
            }
            // One spread, if it's ready, puts these statuses on each living enemy in its reach of the enemy being shot; its cooldown starts
            // when one went on someone.
            void SpreadFrom(int index, (string Status, double Left, double? PerSecond)[] statusesLeft, double at, int? from = null)
            {
                var rule = spreadRules[index];
                if (at < spreadReady[index]) { return; }
                var reached = false;
                // From another enemy, the spread leaves the enemy being shot alone: it takes the skill's own status there (ADR 0016).
                foreach (var (_, enemy) in LivingNear(from ?? target.Place, rule.Within, at, except: from is null ? null : target.Place))
                {
                    foreach (var (status, left, perSecond) in statusesLeft) { reached |= PutStatus(enemy, status, left, at, scale: false, perSecond); }
                }
                if (reached) { spreadReady[index] = at + rule.Every; }
            }
            // A skill's own burn or bleed newly on the enemy being shot (ADR 0015): each spread whose row takes skill statuses copies it to
            // the enemies in reach, for what's left of it up to one application, carrying the skill's own damage where its status has a rate
            // of its own (R105) and the player's otherwise. A spread's one cooldown covers talent and skill statuses alike.
            void SkillStatusOn(string status, double at)
            {
                if (!SkillStatusCopies(status) || statuses?.SkillApplication(status, at) is not { } skill) { return; }
                if (!reachActive || spreadRules.IsEmpty || at >= options.Duration || target.Placeholder || target.Dead || !group!.Alive.Contains(target.Place)) { return; }
                // The copy carries the skill's own damage where its status has a rate of its own (R105); the player's otherwise.
                (string, double, double?)[] left = [(status, Math.Min(skill.Until - at, skill.Span), skill.Rate)];
                for (var index = 0; index < spreadRules.Length; index++)
                {
                    if (spreadRules[index].SkillStatuses && holdingTalents[hand]?.Contains(spreadRules[index].Talent) != false) { SpreadFrom(index, left, at); }
                }
            }
            // A skill's impact with an area (ADR 0016) lands, at full damage, on every living enemy within its radius of the enemy being
            // shot, each on its own pools, with the burn or bleed the impact puts on there for one application, carrying the skill's own
            // damage. It sets off no hit or status event: those stay the enemy being shot's. A kill is a kill elsewhere, settled after the
            // event. Each enemy the status newly lands on, nearest first, can set off a spread that takes skill statuses (ADR 0015).
            void AreaImpact(SkillDamageSchedule.Plan plan, string slot, double at)
            {
                if (!AreaReaches(plan, at)) { return; }
                CheckAreaLimit(plan);
                var landed = new List<(int Place, double? Rate)>();
                foreach (var (place, enemy) in LivingNear(target.Place, plan.Area, at))
                {
                    var (impact, _, statusRate) = enemy.Profile >= 0 && plan.Amounts is { } amounts ? amounts[enemy.Profile] : (plan.Impact, plan.Rate, plan.StatusRate);
                    // Full damage near the centre, falling in a straight line to the edge's share (R107); the status goes on whole.
                    var dealt = enemy.Apply(impact * plan.AreaShare(group!.Distance(target.Place, place, spacing)), 1, plan.ArmorOnly ? 0 : 1);
                    skillTotal += dealt;
                    skillImpactTotal += dealt;
                    total += dealt;
                    timeline[(int)at] += dealt / fights;
                    if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)at]))
                    { throw new SimulationException("damage-overflow", "Skill area damage overflowed"); }
                    Record(DummyEventKind.SkillAreaHit, dealt, at, slot);
                    if (enemy.Dead)
                    {
                        blastKills.Enqueue((enemy, at));
                        continue;
                    }
                    if (plan.Status.Length == 0 || plan.StatusWindow <= 0 || statuses is null) { continue; }
                    LandSkillStatus(plan.Status, plan.StatusWindow, statusRate, place, enemy, at, landed);
                }
                SpreadLanded(plan.Status, plan.StatusWindow, landed, at);
            }
            // A skill with several targets (ADR 0027: the Demolisher Firefly) strikes, as it's used, up to its count less one of the other
            // living enemies, nearest the enemy being shot first, each once at its full impact on its own pools, as a body hit (owner's
            // call after R142). It sets off no hit or status event; a kill is a kill elsewhere, settled after the event.
            void TargetsImpact(SkillDamageSchedule.Plan plan, string slot, double at)
            {
                if (plan.Targets <= 1 || !reachActive || at >= options.Duration || target.Placeholder || !group!.Alive.Contains(target.Place)) { return; }
                foreach (var (_, enemy) in LivingNear(target.Place, double.PositiveInfinity, at).Take(plan.Targets - 1))
                {
                    var impact = enemy.Profile >= 0 && plan.Amounts is { } amounts ? amounts[enemy.Profile].Impact : plan.Impact;
                    var dealt = enemy.Apply(impact, 1, plan.ArmorOnly ? 0 : 1);
                    skillTotal += dealt;
                    skillImpactTotal += dealt;
                    total += dealt;
                    timeline[(int)at] += dealt / fights;
                    if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)at]))
                    { throw new SimulationException("damage-overflow", "Skill target damage overflowed"); }
                    Record(DummyEventKind.SkillAreaHit, dealt, at, slot);
                    if (enemy.Dead) { blastKills.Enqueue((enemy, at)); }
                }
            }
            // ADR 0029 (The Howl): after its hit, a landed impact rolls once on each living enemy it reached, the enemy being shot and those in
            // its area, nearest first, whose profile can die outright and whose health is below full. One that passes dies the kill's delay later.
            void KillRolls(SkillDamageSchedule.Plan plan, string slot, double at)
            {
                if (plan.KillChance <= 0 || at >= options.Duration || target.Placeholder) { return; }
                void Roll(FightEnemy enemy)
                {
                    var profile = enemy.Profile >= 0 ? enemy.Profile : chosenProfile;
                    if (enemy.Dead || !enemy.HealthBelowFull || !plan.KillProfiles.Contains(profile) || deaths.Any(death => ReferenceEquals(death.Enemy, enemy))) { return; }
                    if (killRandom.NextDouble() < plan.KillChance) { deaths.Add((at + plan.KillDelay, enemy, slot)); }
                }
                Roll(target);
                if (!AreaReaches(plan, at)) { return; }
                foreach (var (_, enemy) in LivingNear(target.Place, plan.Area, at)) { Roll(enemy); }
            }
            // A skill's status going on another enemy for this long, carrying the skill's own damage where it has a rate; one newly on is
            // noted, for its spreads.
            void LandSkillStatus(string status, double length, double statusRate, int place, FightEnemy enemy, double at, List<(int Place, double? Rate)> landed)
            {
                var wasOn = enemy.Statuses.Has(status, at);
                double? carried = statusRate > 0 ? statusRate : null;
                if (PutStatus(enemy, status, length, at, scale: false, carried) && !wasOn) { landed.Add((place, carried)); }
            }
            // Each enemy a skill's status newly went on, nearest first, can set off a spread that takes skill statuses (ADR 0015).
            void SpreadLanded(string status, double length, List<(int Place, double? Rate)> landed, double at)
            {
                if (landed.Count == 0 || spreadRules.IsEmpty) { return; }
                foreach (var (place, carried) in landed)
                {
                    for (var index = 0; index < spreadRules.Length; index++)
                    {
                        if (spreadRules[index].SkillStatuses && holdingTalents[hand]?.Contains(spreadRules[index].Talent) != false)
                        { SpreadFrom(index, [(status, length, carried)], at, place); }
                    }
                }
            }
            // A status window with an area (a cloud, ADR 0024) puts its status on every other living enemy within its radius of the enemy being
            // shot as each piece of the window ends, for one application from then, carrying the skill's own damage. So an enemy in reach
            // keeps it to the window's end plus one application, as the enemy being shot does. Nobody walks out: fights don't move enemies.
            // It sets off no event; each enemy it newly lands on, nearest first, can set off a spread that takes skill statuses (ADR 0015).
            void AreaStatus(SkillDamageSchedule.Plan plan, double at, double windowEnd)
            {
                // A piece running past the window's end applies only what's left of the last application. One that killed the enemy
                // being shot reaches nobody from it.
                var left = plan.StatusWindow - Math.Max(0, at - windowEnd);
                if (!plan.Cloud || statuses is null || plan.Status.Length == 0 || left <= 0 || target.Dead || !AreaReaches(plan, at)) { return; }
                CheckAreaLimit(plan);
                var landed = new List<(int Place, double? Rate)>();
                foreach (var (place, enemy) in LivingNear(target.Place, plan.Area, at))
                {
                    var statusRate = enemy.Profile >= 0 && plan.Amounts is { } amounts ? amounts[enemy.Profile].StatusRate : plan.StatusRate;
                    LandSkillStatus(plan.Status, left, statusRate, place, enemy, at, landed);
                }
                SpreadLanded(plan.Status, left, landed, at);
            }
            // A control skill's use (ADR 0017): its status goes on the enemy being shot for the window, as the player's status, through the
            // path talents' statuses take, firing status_applied there if it's newly on; a second use while it's on refreshes it. With an
            // area it goes on every living enemy in reach too, with no events. With no living enemy being shot it goes on nobody.
            //
            // With its area measured from the player (ADR 0035: the Jammer Pulse), an enemy is reached when its own distance from the player
            // is within the area: the fight's distance, or its own under target distances. Without any distance the enemy being shot is
            // taken to be beside the player and the others stand along the line from it, as for other areas. The enemy being shot is
            // reached only within the area, and events stay its own.
            void ControlUse(SkillDamageSchedule.Plan plan, double at)
            {
                if (statuses is null || plan.ControlStatus.Length == 0 || plan.Window <= 0 || at >= options.Duration || !targeted || target.Placeholder || target.Dead) { return; }
                if (plan.AreaFromPlayer) { CheckAreaLimit(plan); }
                var newly = !target.Statuses.Has(plan.ControlStatus, at);
                if ((!plan.AreaFromPlayer || (target.Distance ?? 0) <= plan.Area + 1e-9) &&
                    PutStatus(target, plan.ControlStatus, plan.Window, at, scale: false) && newly && buffs is not null)
                {
                    FireStatuses("status_applied", at, buffWeapon.StatusDurationFactor);
                    if (FireBuffs("status_applied", at, statuses.Active(at))) { Refill(at); }
                }
                if (!AreaReaches(plan, at)) { return; }
                if (!plan.AreaFromPlayer) { CheckAreaLimit(plan); }
                if (plan.AreaFromPlayer && (options.Distance is not null || !distances.IsEmpty))
                {
                    // Each enemy's own distance, read before bringing it in, so one out of reach isn't brought in early.
                    foreach (var place in group!.Alive.Where(place => place != target.Place).ToArray())
                    {
                        if ((distances.IsEmpty ? options.Distance!.Value : distances[place % distances.Length]) > plan.Area + 1e-9) { continue; }
                        var enemy = EnemyAt(place) ?? Early(place, at);
                        if (!enemy.Dead) { PutStatus(enemy, plan.ControlStatus, plan.Window, at, scale: false); }
                    }
                    return;
                }
                foreach (var (_, enemy) in LivingNear(target.Place, plan.Area, at)) { PutStatus(enemy, plan.ControlStatus, plan.Window, at, scale: false); }
            }
            // A control status whose area is measured from the player, landing with no living enemy being shot (ADR 0035, the owner's call
            // after R161): it goes on every living enemy within reach by its own distance, with no events, as an area's others get none.
            // Without a distance there's no enemy being shot to measure the line from, so it reaches nobody; nor without areas reaching
            // others (several at once and a spacing).
            void GapControlUse(SkillDamageSchedule.Plan plan, double at)
            {
                if (statuses is null || plan.ControlStatus.Length == 0 || plan.Window <= 0 || at >= options.Duration || !targeted || !reachActive ||
                    plan.Area <= 0 || options.Distance is null && distances.IsEmpty) { return; }
                CheckAreaLimit(plan);
                foreach (var place in group!.Alive.ToArray())
                {
                    if ((distances.IsEmpty ? options.Distance!.Value : distances[place % distances.Length]) > plan.Area + 1e-9) { continue; }
                    var enemy = EnemyAt(place) ?? Early(place, at);
                    if (!enemy.Placeholder && !enemy.Dead) { PutStatus(enemy, plan.ControlStatus, plan.Window, at, scale: false); }
                }
            }
            // The living enemies within reach of a place on the line, nearest first, each looked up or brought in early as it's reached
            // (ADR 0013), leaving out one place if asked. Lazy, so one an earlier step killed is passed over. A redirect keeps its own
            // filter, since it draws among those already there without bringing anyone in.
            IEnumerable<(int Place, FightEnemy Enemy)> LivingNear(int from, double within, double at, int? except = null)
            {
                foreach (var place in group!.InReach(from, within, spacing))
                {
                    if (place == except) { continue; }
                    var enemy = EnemyAt(place) ?? Early(place, at);
                    if (!enemy.Dead) { yield return (place, enemy); }
                }
            }
            // Whether a skill's area (ADR 0016, ADR 0017) can reach anyone from the enemy being shot now.
            bool AreaReaches(SkillDamageSchedule.Plan plan, double at) =>
                plan.Area > 0 && reachActive && at < options.Duration && !target.Placeholder && group!.Alive.Contains(target.Place);
            // An area over its limit refuses the fight, checked only where the area reaches someone, so a fight it plays no part in isn't refused.
            static void CheckAreaLimit(SkillDamageSchedule.Plan plan)
            {
                if (plan.Area > SimSkillDefinition.MaximumArea)
                { throw new SimulationException("skill-output-invalid", $"{plan.Skill}'s area is over {SimSkillDefinition.MaximumArea:G} metres"); }
            }
            // Settles the kills blasts made, each as any kill on an enemy the player isn't shooting. Under focus fire the next arrival
            // moves past one that died waiting first, so the kill doesn't take the arrival with it.
            void SettleBlastKills()
            {
                while (blastKills.TryDequeue(out var entry))
                {
                    var (dying, at) = entry;
                    // A kill already settled (the enemy being shot dying to its own shot) is gone from the group.
                    if (!dying.Dead || !group!.Alive.Contains(dying.Place)) { continue; }
                    // The enemy being shot can die to a blast another kill set off: that's its own kill, on the spot.
                    KillElsewhere(dying, at);
                }
            }
            // A triggered refill fills the weapon in hand; a reload already under way fills it when it completes.
            // While the shield is raised and its allowed weapon is in hand, the rotation may not draw the other one.
            bool SwapAllowed() => shieldWeapon is null || hand != shieldWeapon || shield?.IsUp != true;
            void Refill(double at)
            {
                if (rotation is not null)
                {
                    // A swap made for the empty magazine or a reload not yet begun has no reason to happen now, so it's taken back.
                    var obsolete = actionEvents.Where(entry => entry.Swap ? !entry.Action.Forced && options.Rotation!.Mode == WeaponSwapMode.WhenEmpty && entry.Action.From == hand :
                        !entry.End && entry.Action.To == hand).Select(entry => entry.Action).FirstOrDefault();
                    if (obsolete is not null) { CancelAction(obsolete); }
                    rotation.Refill(hand, at);
                }
                else if (!initialWeapon.NoReload && !reloadCompleting) { Fill(at); }
                RefillHolstered(at);
            }
            // A refill of both (refill "both", ADR 0019) fills a rotation's holstered weapon too, and the request is taken either way. The
            // holstered weapon never reloads while away; a reload booked for its arrival comes only with a swap, which an empty swap's
            // cancel takes back, and a forced swap is booked after the events at its time.
            void RefillHolstered(double at)
            {
                if (refillBoth) { rotation?.RefillAll(at); }
                refillBoth = false;
            }
            // ADR 0036: the mark on the enemy being shot now, while its use runs; null for none.
            ZoneMark? MarkOn(double at)
            {
                if (targeted && target.Dead) { return null; }
                foreach (var (index, mark) in zoneMarks)
                {
                    if (ReferenceEquals(mark.Enemy, target) && skills!.LastUse(index) == mark.Use && !double.IsNaN(skills.RunningUntil(index, at))) { return mark; }
                }
                return null;
            }
            // When the mark on the enemy being shot ends, after this time: its key moves the state then.
            double NextMarkEnd(double at)
            {
                var ends = double.PositiveInfinity;
                foreach (var (index, mark) in zoneMarks)
                {
                    if (ReferenceEquals(mark.Enemy, target) && skills!.LastUse(index) == mark.Use && skills.RunningUntil(index, at) is var end && end > at) { ends = Math.Min(ends, end); }
                }
                return ends;
            }
            // On the marked enemy the mark's key reads true, over the user's own setting.
            ImmutableDictionary<string, string> Marked(ImmutableDictionary<string, string> read, double at) =>
                zoneKey.Length > 0 && MarkOn(at) is not null ? read.SetItem(zoneKey, "true") : read;
            void RefreshWeapon(double at)
            {
                if (buffs is null) { return; }
                var phased = OnTarget(rotation is null ? phases!.Context(context, at, ammo, filled) : HandPhases(hand, at));
                heldMagazine?.Observe(phased, Math.Min(at, options.Duration));
                // While waiting for the next target there is none to read; it arrives at full pools, never 0%.
                var distanceNow = DistanceNow();
                ImmutableDictionary<string, string> Changed()
                {
                    var read = combat!.Context(phased, distanceNow ?? 0, target.PelletHits[hand], targeted && !target.Dead ? target.RemainingPercent() : 100, BonusArmorPercent(at));
                    var held = InHand(buffs.Context(statuses!.Context(read, at), at), hand);
                    return Marked(headshots?.Context(held) ?? held, at);
                }
                var changed = Changed();
                var state = StateAt(changed, distanceNow);
                if (state != lastState)
                {
                    var before = BonusArmorPercent(at);
                    Resolve(state, changed, distanceNow);
                    FollowBonusArmor(at);
                    // A state that moved the player's bonus armor (ADR 0020) is read once more, so a bonus armor reading (ADR 0021) sees it.
                    if (readsBonusArmor && BonusArmorPercent(at) != before)
                    {
                        changed = Changed();
                        state = StateAt(changed, distanceNow);
                        if (state != lastState)
                        {
                            Resolve(state, changed, distanceNow);
                            FollowBonusArmor(at);
                        }
                    }
                }
                target.Dots?.Sync(statuses!, statusTicks);
            }
            // The cycle's armor step (ADR 0021): a repair of its share of max armor, or at full armor as much again as lasting bonus armor,
            // while bonus armor is below its cap of max armor. Without the player's pools it does nothing.
            void CycleArmor(double at)
            {
                if (player is null || player.Downed || at >= options.Duration) { return; }
                if (!player.ArmorFull)
                {
                    var repair = player.Repair(player.MaxArmor * cycleRepair, 0, at);
                    RecordRepair(repair.Armor, 0, at);
                }
                else if (player.BonusArmorAt(at) < cycleCap * player.MaxArmor)
                {
                    var added = player.AddLastingBonusArmor(player.MaxArmor * cycleRepair);
                    if (added > 0) { FlushRegeneration(); Record(DummyEventKind.BonusArmorGranted, added, at, ""); }
                }
            }
            // The player's bonus armor as a percentage of max armor, for bonus armor readings (ADR 0021); none without the player's pools.
            double? BonusArmorPercent(double at) => player is null ? null : player.MaxArmor > 0 ? player.BonusArmorAt(at) / player.MaxArmor * 100 : 0;
            // The player's bonus armor follows the state (ADR 0020): a rise is gained as a grant is, and lasts while the state holds.
            void FollowBonusArmor(double at)
            {
                // As a grant, nothing is gained once the fight is over.
                if (player is null || enemyProfile is null || at >= options.Duration) { return; }
                var gained = player.FollowStateBonusArmor(enemyProfile.BonusArmor);
                if (gained > 0) { FlushRegeneration(); Record(DummyEventKind.BonusArmorGranted, gained, at, ""); }
            }
            // A status fixes its amount from the state before this shot, as its duration does, so it reads a bonus its own event spends
            // (Perpetuation's, R61), and a bonus this same bullet earned waits for the next status.
            void FixNewStatus() => target.Dots?.Sync(statuses!, statusTicks);
            // The copy in hand of a talent on both weapons supplies its keys; the other copy's keys never reach resolution.
            ImmutableDictionary<string, string> InHand(ImmutableDictionary<string, string> driven, string slot)
            {
                foreach (var (name, copies) in copiedKeys)
                {
                    driven = driven.TryGetValue(copies[slot], out var value) ? driven.SetItem(name, value) : driven.Remove(name);
                    driven = driven.RemoveRange(copies.Values);
                }
                return driven;
            }
            // Phase readings with a weapon in hand under rotation: magazine rules read that weapon's rounds, and a magazine
            // rule owned by the other (holstered) weapon is unset, as Go switched holstered readings off.
            ImmutableDictionary<string, string> HandPhases(string slot, double at)
            {
                var read = phases!.Context(handContexts[slot], at, rotation!.ReadableRounds(slot, at), rotation.Capacity(slot, at));
                foreach (var rule in magazineRules)
                {
                    if (weaponTalents.GetValueOrDefault(slot)?.Contains(rule.Talent) != true && weaponTalents.Any(pair => pair.Key != slot && pair.Value.Contains(rule.Talent))) { read = read.Remove(rule.Key); }
                }
                return read;
            }
            // The driven state with a weapon in hand, at the current trigger state: the hand's own, or another weapon's
            // for a rotation deciding a swap to it. Only the hand's state is applied.
            ShotProfile HeldProfile(string slot, double at)
            {
                if (slot == hand) { return buffWeapon; }
                var distanceNow = DistanceNow();
                var changed = StateHeld(slot, at);
                return Lookup(StateAt(changed, distanceNow), changed, slot, distanceNow).Profile;
            }
            // The stats a gated rule reads. The fight's last state hasn't seen the bullet's own events yet, nor a kill's next enemy. A swap
            // refreshes first, so its source weapon, out of hand by then, is read as held.
            ImmutableDictionary<string, double> StatsBefore(string slot, double at)
            {
                var distanceNow = DistanceNow();
                if (slot == hand) { return Lookup(lastState, lastChanged, hand, distanceNow).Stats; }
                var changed = StateHeld(slot, at);
                return Lookup(StateAt(changed, distanceNow), changed, slot, distanceNow).Stats;
            }
            // The driven state with a weapon in hand. A read: nothing is applied.
            ImmutableDictionary<string, string> StateHeld(string slot, double at)
            {
                var phaseContext = OnTarget(rotation is null ? phases!.Context(context, at, ammo, filled) : HandPhases(slot, at));
                phaseContext = combat!.Context(phaseContext, DistanceNow() ?? 0, target.PelletHits[slot], targeted && !target.Dead ? target.RemainingPercent() : 100, BonusArmorPercent(at));
                var held = InHand(buffs!.Context(statuses!.Context(phaseContext, at), at), slot);
                return Marked(headshots?.Context(held) ?? held, at);
            }
            // A skill reads the build as it deploys, or at each batch for a live rate, with the weapon in hand (ADR 0012). A use keeps the rest.
            SkillDamageSchedule SkillsInState(string state, ImmutableDictionary<string, string> changed)
            {
                if (skillStates.TryGetValue(state, out var cached)) { return cached; }
                SkillDamageSchedule Read(ImmutableDictionary<string, string> at)
                {
                    var dynamic = BuildResolver.Resolve(pack, build, at);
                    if (dynamic.Diagnostics.Any(d => d.Severity == "error")) { throw new SimulationException("resolve-failed", "Triggered state failed resolution"); }
                    return SkillDamageSchedule.Create(pack.Pack, dynamic.ActiveSkillPreviews, options.IncludeShield, at, FightDistance(options), options.IncludeIncoming, options.DriveSimpleBuffs);
                }
                var skills = Read(changed);
                if (!sequence.IsEmpty)
                {
                    var profiles = sequence.Distinct().ToDictionary(profile => profile, profile => Read(changed.SetItem(TargetProfileKey, profile.ToString(CultureInfo.InvariantCulture))));
                    if (profiles.Values.Any(other => !skills.SameTiming(other)))
                    { throw new SimulationException("target-sequence-unsupported", "Skill timing, or which kinds of damage a skill deals, changes with the target profile, which mixed targets can't model yet"); }
                    skills = skills.WithAmounts(profiles);
                }
                return skillStates[state] = skills;
            }
            SkillDamageSchedule SkillsAt(double at)
            {
                if (buffs is null) { return heldSkills[rotation?.ActiveSlot ?? active]; }
                var changed = StateHeld(hand, at);
                return SkillsInState(StateAt(changed, DistanceNow()), changed);
            }
            // The fire behind the next attempt: the state's own, or in a group the same state against the enemy firing, as its own
            // profile and from its own distance.
            EnemyFireSchedule Shooter()
            {
                if (group is null) { return enemyProfile!; }
                int? profile = sequence.IsEmpty ? null : sequence[group.Firing % sequence.Length];
                double? meters = distances.IsEmpty ? null : distances[group.Firing % distances.Length];
                if ((profile is null || profile == TargetInPlay()) && (meters is null || meters == DistanceNow())) { return enemyProfile!; }
                if (!shooters.TryGetValue((profile, meters), out var shooter))
                {
                    var changed = lastChanged;
                    if (profile is { } own) { changed = changed.SetItem(TargetProfileKey, own.ToString(CultureInfo.InvariantCulture)); }
                    if (meters is { } away)
                    {
                        // Distance rules and bands read this enemy's distance, not the one being shot; what it holds isn't read (ADR 0011).
                        // The state's own bonus armor reading stands: another enemy's fire reads the same player.
                        changed = combat!.Context(changed, away, target.PelletHits[hand], 100);
                        if (distanceBands.Count > 0) { changed = AtDistance(changed, away); }
                    }
                    var distance = meters ?? DistanceNow();
                    shooters[(profile, meters)] = shooter = Lookup(StateAt(changed, distance), changed, hand, distance).Enemy!;
                }
                return shooter;
            }
            void Resolve(string state, ImmutableDictionary<string, string> changed, double? distance)
            {
                var cached = Lookup(state, changed, hand, distance);
                lastChanged = changed;
                shooters.Clear();
                buffWeapon = cached.Profile;
                enemyProfile = cached.Enemy;
                recoveryProfile = cached.Recovery;
                statusTicks = cached.Ticks;
                lastState = state;
            }
            (ShotProfile Profile, EnemyFireSchedule? Enemy, RecoveryProfile? Recovery, ImmutableDictionary<string, double> Ticks, ImmutableDictionary<string, double> Stats)
                Lookup(string state, ImmutableDictionary<string, string> changed, string slot, double? distance)
            {
                if (!resolvedStates.TryGetValue(state, out var cached))
                {
                    var dynamic = BuildResolver.Resolve(pack, build, changed);
                    if (dynamic.Diagnostics.Any(d => d.Severity == "error")) { throw new SimulationException("resolve-failed", "Triggered state failed resolution"); }
                    var profile = ShotProfile.Read(dynamic, targeted, distance, zones);
                    var held = profiles[slot];
                    // Fire rate and reload time are read as each interval or reload starts (Measured, Frenzy, On Empty), and a weapon's
                    // magazine size at its next fill (ADR 0025: Overflowing). With a stack cap sized from the magazine, a magazine change
                    // isn't scheduled yet; pellets and target pools would change state already committed.
                    var stateProfile = sequence.IsEmpty ? -1 : int.Parse(changed[TargetProfileKey], CultureInfo.InvariantCulture);
                    var (stateArmor, stateHealth) = stateProfile < 0 ? (armor, health) : sequencePools[stateProfile];
                    if (profile.Magazine != held.Magazine && magazineCapped || profile.NoReload != held.NoReload || profile.Pellets != held.Pellets ||
                        targeted && (dynamic.Calculation.Stats.GetValueOrDefault("enemy_target_armor") != stateArmor || dynamic.Calculation.Stats.GetValueOrDefault("enemy_target_health") != stateHealth))
                    { throw new SimulationException("buff-timing-unsupported", "Triggered magazine, pellet or target-pool changes need dynamic scheduling"); }
                    var dynamicEnemy = incoming is not null ? EnemyFireSchedule.Create(dynamic) : null;
                    RecoveryProfile? dynamicRecovery = null;
                    if (dynamicEnemy is not null)
                    {
                        dynamicRecovery = ReadRecovery(dynamic);
                        // The drain's seconds run only when a weapon carries it as the fight starts.
                        if (shieldProfile is not null && !drainUsed && dynamicRecovery.ShieldDrainShare > 0)
                        { throw new SimulationException("incoming-repair-unsupported", "A shield drain that only a fight's state turns on isn't scheduled (ADR 0026)"); }
                        // A state's fire must match its target's (the chosen one's without a sequence): triggers may not change it, nor
                        // the player's armor or health. Bonus armor may change; the pools follow it as the state applies (ADR 0020).
                        if (!(stateProfile < 0 ? incoming! : sequenceFire[stateProfile]).SameClockAndPools(dynamicEnemy, hitRateFollows, anyBonusArmor: true))
                        { throw new SimulationException("incoming-state-unsupported", "Triggered enemy cadence, hit probability, attackers, player armor or health changes require dynamic scheduling"); }
                    }
                    if (shieldProfile is not null && ShieldProfile.Read(pack.Pack, dynamic, active, requireAllowed: rotation is null) != shieldProfile)
                    { throw new SimulationException("shield-state-unsupported", "Triggered shield health, regeneration or cooldown changes require dynamic scheduling"); }
                    resolvedStates[state] = cached = (profile, dynamicEnemy, dynamicRecovery, ReadStatusTicks(dynamic), dynamic.Calculation.Stats);
                    diagnostics = [.. diagnostics.Concat(dynamic.Diagnostics).Distinct()];
                }
                return cached;
            }
            // A completed reload fires reload_end for rules that read it (reload-finished talents).
            void FinishReload(double at)
            {
                if (buffs is null || !reloadEndUsed || at >= options.Duration) { return; }
                FireStatuses("reload_end", at, buffWeapon.StatusDurationFactor);
                if (FireBuffs("reload_end", at, statuses!.Active(at), weapon: hand)) { Refill(at); }
                RefreshWeapon(at);
            }
            double StartReload()
            {
                RefreshWeapon(time);
                var reloadDuration = buffs is null ? initialWeapon.Reload : buffWeapon.Reload;
                reloads++;
                if (buffs is not null)
                {
                    FireStatuses("reload", time, buffWeapon.StatusDurationFactor);
                    FireBuffs("reload", time, statuses!.Active(time), weapon: hand);
                    // A reload started from an empty magazine (R138: Overflowing counts only these) is also reload_empty, after reload, so
                    // what a reload spends is spent before an empty one gains.
                    if (ammo < 1)
                    {
                        FireStatuses("reload_empty", time, buffWeapon.StatusDurationFactor);
                        FireBuffs("reload_empty", time, statuses!.Active(time), weapon: hand);
                    }
                }
                RefreshWeapon(time);
                Record(DummyEventKind.ReloadStarted);
                var completed = time + reloadDuration;
                if (!double.IsFinite(completed) || reloadDuration > 0 && completed <= time)
                { throw new SimulationException("time-not-advancing", "The reload duration cannot advance time"); }
                return completed;
            }
            // Only steps that do fight work spend budget; the deferred reload start and completion are
            // bookkeeping. Enemy attempts and the state boundaries crossed while armor regenerates also spend.
            void Spend()
            {
                if (++work > WorkLimit) { throw new SimulationException("work-limit", "The simulation exceeded its bounded work budget"); }
            }
            // A driven rotation decides at the weapon branch's clock: each weapon's reload and swap speed as it would be
            // in hand then. The holstered weapon's are its current state; the drawn weapon's are resolved with it in hand.
            // ADR 0030: each plan used only once the player's armor is below a share of its maximum, by its index. Only a fight under
            // enemy fire has one; the others step aside.
            IReadOnlyDictionary<int, double> armorWaits = skills is null || player is null ? new Dictionary<int, double>() : skills.ArmorWaits();
            // ADR 0030: each repair skill use's repairs left and when it may next repair, by slot and use time.
            var repairUses = new Dictionary<(string Slot, double Use), RepairUse>();
            // ADR 0031: a hive charging skills: when each target use may next take a charge, by the target's index and use (the mark is
            // on the target, so the hive's uses and its holstered pulses share it), and the targets of the charges in flight, in launch
            // order, by the hive's slot and landing time.
            var targetReady = new Dictionary<(int Target, double TargetUse), double>();
            // ADR 0032: skills whose uses hold a buff (skill_out).
            var holdingSkills = fightBuffRules.Where(rule => rule.On == "skill_out").Select(rule => rule.Talent).ToHashSet(StringComparer.Ordinal);
            // ADR 0033: the keys of those buffs that refill the weapon put away on each swap while on.
            var swapRefillKeys = fightBuffRules.Where(rule => rule.RefillOnSwap).Select(rule => rule.Key).Distinct(StringComparer.Ordinal).ToArray();
            // ADR 0034: each decoy use's health left, by plan index and use time; the plans that draw fire.
            var decoyLeft = new Dictionary<(int Index, double Use), double>();
            var decoyPlans = skills is null ? [] : Enumerable.Range(0, skills.Plans.Length)
                .Where(index => pack.Pack.Simulation.Skills.Any(rule => rule.Skill == skills.Plans[index].Skill && rule.DecoyHealth.Length > 0)).ToArray();
            var inFlight = new Dictionary<(string Slot, double At), Queue<int>>();
            // ADR 0036: a use marking zones has no events of its own either, so it deploys as the fight reaches it.
            var marksZones = skills is not null && skills.Plans.Any(plan => plan.MarksZones);
            // ADR 0037: the plans reviving the player, whose uses have no events of their own either.
            var revivers = skills is null ? [] : Enumerable.Range(0, skills.Plans.Length).Where(index => skills.Plans[index].Revive is not null).ToArray();
            // ADR 0031: a buff hive's charge fires skill_charge for the skill's own rules alone, then the state reads what they turned on.
            void FireCharge(string skill, double at)
            {
                var owner = new HashSet<string>(StringComparer.Ordinal) { skill };
                if (FireBuffs("skill_charge", at, statuses!.Active(at), owner, owner)) { Refill(at); }
                RefreshWeapon(at);
            }
            // A decoy (ADR 0034) has no events of its own, so its uses must deploy as the fight reaches them, or a static fight would make
            // them all ahead and find only the last running.
            if (skills is not null && (buffs is not null || rotation is not null || armorWaits.Count > 0 || decoyPlans.Length > 0 || marksZones || revivers.Length > 0)) { skills.Read = (slot, at) => SkillsAt(at).For(slot); }
            if (skills is not null && (!skillUse.IsEmpty || armorWaits.Count > 0))
            {
                // Each plan waiting for a key or for enemies, by its index.
                var waitingFor = new Dictionary<int, SkillUseCondition>();
                foreach (var (slot, condition) in skillUse)
                {
                    var index = skills.IndexOf(slot);
                    if (condition.When == SkillUseCondition.AfterSeconds) { skills.DelayFirst(index, condition.Seconds); }
                    if (condition.When is SkillUseCondition.KeyOn or SkillUseCondition.EnemiesAtLeast) { waitingFor[index] = condition; }
                }
                // A key is on when the state the skill would read holds it as anything but off; enemies count while alive in the group.
                // A skill waiting for armor needs both its own condition and the armor's.
                if (waitingFor.Count > 0 || armorWaits.Count > 0)
                {
                    // An enemy killed at this moment counts only if nobody is left to take its place.
                    skills.MayDeploy = (index, at) => (!armorWaits.TryGetValue(index, out var share) || player!.ArmorFraction < share) &&
                        (!waitingFor.TryGetValue(index, out var condition) || condition.When switch
                        {
                            SkillUseCondition.KeyOn => IsOn(StateHeld(hand, at).GetValueOrDefault(condition.Key)),
                            SkillUseCondition.EnemiesAtLeast => group!.Living(place => EnemyAt(place)?.Dead == true) >= condition.Count,
                            _ => throw new InvalidOperationException($"No hold for a {condition.When} skill use")
                        });
                }
            }
            if (rotation is not null && options.DriveSimpleBuffs)
            {
                rotation.DrivenTiming = slot =>
                {
                    var profile = HeldProfile(slot, time);
                    return (profile.Reload, profile.SwapSpeed, profile.SwitchMultiplier);
                };
                rotation.DrivenMagazine = (slot, at) => HeldProfile(slot, at).Magazine;
            }
            // Arrival traces may have been scheduled before the fight ended early.
            void TrimTrace(double at)
            {
                for (var index = events.Count - 1; fight == 0 && index >= 0; index--)
                { if (events[index].Time > at) { events.RemoveAt(index); } }
            }
            // ADR 0037: whether a skill out will revive the player, who has just gone down. Its use is spent and ends a while after the
            // revive; a reload or armor kit under way stops, and the weapon waits. (The game ends the player's other hives too; a build has one.)
            bool GoDown(double at)
            {
                foreach (var index in revivers)
                {
                    if (double.IsNaN(skills!.RunningUntil(index, at))) { continue; }
                    var plan = skills.LastPlan(index) ?? skills.Plans[index];
                    var rule = plan.Revive!;
                    var reviveAt = at + plan.ReviveSeconds;
                    if (!double.IsFinite(reviveAt) || reviveAt <= at) { throw new SimulationException("time-not-advancing", "A revive cannot advance time"); }
                    FlushRegeneration();
                    player!.AwaitRevive();
                    down = new(at, reviveAt, rule, plan.ReviveArmor, player.MaxHealth * rule.PoolShare);
                    skills.EndEarly(index, skills.LastUse(index), reviveAt + rule.EndAfter);
                    var kitStopped = kits?.Cancel() == true;
                    if (kitStopped) { kitsCancelled++; }
                    if (rotation is not null)
                    {
                        // One ending at this very moment hasn't ended for the fight, whose hits come first.
                        if (lastAction is { } action && action.ReadyAt >= at) { DeferAction(action, at, reviveAt); }
                        // A kit under way had stopped the weapon's clock at its own end.
                        time = kitStopped ? reviveAt : Math.Max(time, reviveAt);
                    }
                    else if (reloadCompleting)
                    {
                        // It starts over once the player is back, from what the magazine holds, unless that's full by then.
                        reloadCompleting = false;
                        reloadStarting = reloadRestarting = true;
                        time = reviveAt;
                    }
                    else { time = kitStopped ? reviveAt : Math.Max(time, reviveAt); }
                    return true;
                }
                return false;
            }
            // ADR 0037: damage on a downed player, at its own share, against the downed pool the player is also bleeding from. Returns the
            // damage taken and whether they died of it.
            (double Taken, bool Died) TakeWhileDown(double amount, double at, DummyEventKind kind)
            {
                var left = Math.Max(0, down!.Left(at));
                var dealt = amount * down.Rule.DamageTaken;
                var taken = Math.Min(left, dealt);
                down.Taken += taken;
                incomingDamage[fight] += taken;
                if (!double.IsFinite(incomingDamage[fight])) { throw new SimulationException("damage-overflow", "Incoming damage overflowed"); }
                Record(kind, taken, at, "");
                RefreshWeapon(at);
                if (dealt < left) { return (taken, false); }
                Perish(at);
                return (taken, true);
            }
            void Perish(double at)
            {
                player!.Perish(at);
                down = null;
                Record(DummyEventKind.Died, at: at, slot: "");
                TrimTrace(at);
            }
            // Damage on the player from an enemy's hit or a status's tick (ADR 0018): through bonus armor, armor then health, with the same
            // repairs and talent events. Only a hit extends an armor kit's use. Returns the damage taken and whether the player went down.
            (double Taken, bool Downed) TakeDamage(double amount, double at, DummyEventKind kind)
            {
                var hit = player!.Apply(amount, at, recoveryProfile!.BreakArmor);
                if (!hit.Downed && hit.DamageTaken > 0 && recoveryProfile.MakeshiftShare > 0)
                { queuedRepairs.Enqueue(hit.DamageTaken * recoveryProfile.MakeshiftShare, recoveryProfile.MakeshiftDuration, at); }
                incomingDamage[fight] += hit.DamageTaken;
                if (!double.IsFinite(incomingDamage[fight])) { throw new SimulationException("damage-overflow", "Incoming damage overflowed"); }
                Record(kind, hit.DamageTaken, at, "");
                if (kind == DummyEventKind.EnemyHit && kits?.Hit(at) is { } kitEnds) { time = kitEnds; }
                // A skill waiting for the player's armor to fall may be used now (ADR 0030). A fight driving buffs releases it as the
                // hit's own events have run, so a key they change is read after them.
                if (hit.DamageTaken > 0 && armorWaits.Count > 0 && buffs is null) { skills!.Release(at); }
                var enemyRefill = false;
                if (buffs is not null && hit.DamageTaken > 0)
                {
                    FireStatuses("damaged", at, buffWeapon.StatusDurationFactor);
                    enemyRefill |= FireBuffs("damaged", at, statuses!.Active(at));
                }
                if (hit.ArmorBroken)
                {
                    armorBreaks++;
                    Record(DummyEventKind.ArmorBreak, at: at, slot: "");
                    RecordRepair(hit.ArmorRepaired, 0, at);
                    if (buffs is not null)
                    {
                        FireStatuses("armor_break", at, buffWeapon.StatusDurationFactor);
                        enemyRefill |= FireBuffs("armor_break", at, statuses!.Active(at));
                    }
                }
                RefreshWeapon(at);
                // A reload already under way fills the magazine when it completes; until then no rounds are
                // usable, so phase readings keep seeing an empty magazine. A pending reload start still sees it.
                if (enemyRefill) { Refill(at); RefreshWeapon(at); }
                if (hit.Downed)
                {
                    Record(DummyEventKind.Downed, at: at, slot: "");
                    if (GoDown(at)) { return (hit.DamageTaken, false); }
                    TrimTrace(at);
                }
                return (hit.DamageTaken, hit.Downed);
            }
            // The profile of the enemy firing now: the chosen target's, or in a sequence (which always fights as a group) the firing enemy's.
            int FiringProfile() => sequence.IsEmpty ? chosenProfile : sequence[group!.Firing % sequence.Length];
            // A landed hit puts its enemy's statuses on the player (ADR 0018), by the profile firing it.
            void PutStatusesOn(double at, int profile)
            {
                if (playerStatuses is null || !enemyStatusRows.TryGetValue(profile, out var rows)) { return; }
                foreach (var (status, duration, tick, period, tickOnApply) in rows) { playerStatuses.Apply(status, at, duration, tick, period, tickOnApply); }
            }
            while (time < options.Duration || skills?.Next is not null || nextEnemy < options.Duration || Ticking()?.Dots!.NextTick < options.Duration ||
                pickups.Count > 0 && pickups.Peek() < options.Duration || actionEvents.Count > 0 && actionEvents.Peek().At < options.Duration || nextSecond < options.Duration ||
                HeldDue() < options.Duration || blastKills.Count > 0 || playerStatuses?.NextTick < options.Duration || NextDeath() < options.Duration || down?.Due < options.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blastKills.Count > 0)
                {
                    SettleBlastKills();
                    continue;
                }
                var nextSkill = skills?.Next;
                var nextSkillTime = nextSkill?.Time ?? double.PositiveInfinity;
                var nextStatusTick = Ticking()?.Dots!.NextTick ?? double.PositiveInfinity;
                // A pickup due at or after the end is never collected, so it never holds other events back.
                var nextPickup = pickups.Count > 0 && pickups.Peek() < options.Duration ? pickups.Peek() : double.PositiveInfinity;
                // A queued swap or reload start waits for every other event at its time.
                var nextAction = actionEvents.Count > 0 ? actionEvents.Peek().At : double.PositiveInfinity;
                var tickBeforeEnemy = nextSkill?.Kind.IsBatch() == true && nextSkillTime == nextEnemy;
                // A held cooldown grant lands only when nothing else is due by then, so every event at its time comes first.
                var heldDue = HeldDue();
                // A status's tick on the player (ADR 0018) lands only when nothing else is due by then: every other event at its time,
                // the weapon's shot included, comes first.
                var nextPlayerTick = playerStatuses?.NextTick ?? double.PositiveInfinity;
                // An outright kill (ADR 0029) lands once nothing else is due by its time. An enemy dead by then, or gone, is passed over.
                var nextDeath = NextDeath();
                // ADR 0037: a downed player is revived, or bleeds out, ahead of anything else then.
                if (down is not null && down.Due is var downDue && downDue < options.Duration && downDue <= time && downDue <= nextEnemy && downDue <= nextSkillTime && downDue <= nextStatusTick &&
                    downDue <= nextPickup && downDue <= nextAction && downDue <= nextSecond && downDue <= heldDue && downDue <= nextPlayerTick && downDue <= nextDeath)
                {
                    Spend();
                    Regenerate(downDue);
                    if (down.DeathAt < down.ReviveAt)
                    {
                        RefreshWeapon(downDue);
                        Perish(downDue);
                        break;
                    }
                    var back = player!.Revive(down.Armor, downDue);
                    immuneUntil = downDue + down.Rule.ImmuneSeconds;
                    down = null;
                    revives++;
                    Record(DummyEventKind.Revived, at: downDue, slot: "");
                    RecordRepair(back.Armor, back.Health, downDue);
                    RefreshWeapon(downDue);
                    continue;
                }
                if (nextDeath < options.Duration && nextDeath < time && nextDeath < nextEnemy && nextDeath < nextSkillTime && nextDeath < nextStatusTick &&
                    nextDeath < nextPickup && nextDeath < nextAction && nextDeath < nextSecond && nextDeath < heldDue && nextDeath < nextPlayerTick)
                {
                    Spend();
                    var index = deaths.FindIndex(death => death.At == nextDeath);
                    var (at, dying, slot) = deaths[index];
                    deaths.RemoveAt(index);
                    if (!Regenerate(at)) { RefreshWeapon(at); }
                    FlushRegeneration();
                    var stillThere = ReferenceEquals(dying, target) || group is not null && group.Alive.Contains(dying.Place) && left.ContainsKey(dying.Place);
                    if (dying.Dead || dying.Placeholder || !stillThere) { continue; }
                    var dealt = dying.Finish();
                    skillTotal += dealt;
                    skillImpactTotal += dealt;
                    total += dealt;
                    timeline[(int)at] += dealt / fights;
                    if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)at])) { throw new SimulationException("damage-overflow", "Outright kill damage overflowed"); }
                    Record(DummyEventKind.OutrightKill, dealt, at, slot);
                    KillElsewhere(dying, at, talents: false);
                    continue;
                }
                if (nextPlayerTick < options.Duration && nextPlayerTick < time && nextPlayerTick < nextEnemy && nextPlayerTick < nextSkillTime &&
                    nextPlayerTick < nextStatusTick && nextPlayerTick < nextPickup && nextPlayerTick < nextAction && nextPlayerTick < nextSecond && nextPlayerTick < heldDue)
                {
                    Spend();
                    var at = nextPlayerTick;
                    if (!Regenerate(at)) { RefreshWeapon(at); }
                    FlushRegeneration();
                    var (_, amount) = playerStatuses!.Take(at);
                    // Nothing hurts a player just revived (ADR 0037).
                    if (at < immuneUntil) { RefreshWeapon(at); continue; }
                    var (taken, tickDowned) = down is null ? TakeDamage(amount, at, DummyEventKind.PlayerStatusTick) : TakeWhileDown(amount, at, DummyEventKind.PlayerStatusTick);
                    statusDamageTaken[fight] += taken;
                    if (tickDowned) { break; }
                    continue;
                }
                if (heldDue < options.Duration && heldDue < time && heldDue < nextEnemy && heldDue < nextSkillTime && heldDue < nextStatusTick &&
                    heldDue < nextPickup && heldDue < nextAction && heldDue < nextSecond)
                {
                    Spend();
                    if (!Regenerate(heldDue)) { RefreshWeapon(heldDue); }
                    var landing = holds.FindIndex(hold => hold.Start + hold.Delay == heldDue);
                    var (share, seconds, _, _, _) = holds[landing];
                    holds.RemoveAt(landing);
                    CutCooldowns(heldDue, share, seconds);
                    RefreshWeapon(heldDue);
                    continue;
                }
                if (nextEnemy < options.Duration && nextEnemy <= time && nextEnemy <= nextSkillTime && nextEnemy <= nextStatusTick && nextEnemy <= nextPickup && nextEnemy <= nextSecond && nextEnemy <= nextAction && !tickBeforeEnemy)
                {
                    Spend();
                    var at = nextEnemy;
                    if (!Regenerate(at))
                    {
                        if (target.Dead && at >= available)
                        {
                            target = NewTarget();
                            arrival = available;
                        }
                        RefreshWeapon(at);
                    }
                    FlushRegeneration();
                    enemyAttempts++;
                    Record(DummyEventKind.EnemyShot, at: at, slot: "");
                    // Every attempt draws its roll, so blocking one never changes whether a later attempt lands.
                    var roll = enemyRandom.NextDouble();
                    // Behind cover every attempt is blocked, and shock credit doesn't build (ADR 0009).
                    // A downed player stays where they went down (ADR 0037).
                    if (cycle?.Behind(down?.Since ?? at) == true)
                    {
                        blockedShots++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    // A shocked enemy's own fire is what shock stops; a disrupted one's too where disrupt stops its profile's fire (ADR 0035:
                    // machines), through the same credit, so an enemy both shocked and disrupted is blocked once.
                    var firing = group is null ? target : EnemyAt(group.Firing);
                    var shooter = Shooter();
                    if (firing?.Shock.Blocks(firing.Statuses.Has("shocked", at) || shooter.DisruptStopsFire && firing.Statuses.Has("disrupted", at)) == true)
                    {
                        blockedShots++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    // Peeking from cover, a share of the normal hit chance lands.
                    var landed = roll < shooter.HitProbability * (options.PeekHitShare ?? 1);
                    // ADR 0034: a decoy out draws the fire: a landing hit spends its health by the decoy's share of the raw damage, and a
                    // decoy spent ends its use now, fire returning to the player. As a simplification, behind cover the attempt is blocked
                    // as cover blocks it, and peeking's share applies, as for a hit on the player; an attempt at the moment the decoy
                    // deploys still goes to the player.
                    var drawing = -1;
                    foreach (var index in decoyPlans) { if (!double.IsNaN(skills!.RunningUntil(index, at))) { drawing = index; break; } }
                    if (drawing >= 0)
                    {
                        if (landed)
                        {
                            drawnHits++;
                            var use = (drawing, skills!.LastUse(drawing));
                            // Its health is the one it deployed with, in the state then.
                            var decoyHealth = decoyLeft.GetValueOrDefault(use, (skills.LastPlan(drawing) ?? skills.Plans[drawing]).DecoyHealth) - shooter.RawDamagePerHit * shooter.DecoyShare;
                            decoyLeft[use] = decoyHealth;
                            Record(DummyEventKind.EnemyShotDrawn, at: at, slot: skills.Plans[drawing].Slot);
                            if (decoyHealth <= 0) { skills.EndNow(drawing, at); }
                        }
                        AdvanceEnemy(at);
                        continue;
                    }
                    // ADR 0034: a share of landing hits removed before they reach the player (the Trophy System's reflections), read off the
                    // same roll, so no draw moves: the lowest share of the landing range is removed, as a blocked attempt.
                    if (landed && roll < shooter.HitProbability * (options.PeekHitShare ?? 1) * shooter.RemovedShare)
                    {
                        removedHits++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    // Nothing hurts a player just revived (ADR 0037); a raised shield still takes its hits.
                    if (landed && at < immuneUntil && shield?.IsUp != true)
                    {
                        blockedShots++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    if (landed)
                    {
                        enemyHits++;
                        if (shield?.IsUp == true && down is null)
                        {
                            FlushShieldRepair();
                            var shieldHit = shield.Apply(shooter.RawDamagePerHit, at);
                            shieldAbsorbed[fight] += shieldHit.Absorbed;
                            if (!double.IsFinite(shieldAbsorbed[fight])) { throw new SimulationException("damage-overflow", "Shield absorption total overflowed"); }
                            Record(DummyEventKind.ShieldHit, shieldHit.Absorbed, at, shieldProfile!.Slot);
                            if (shieldHit.Absorbed > 0 && recoveryProfile!.MakeshiftShare > 0)
                            { queuedRepairs.Enqueue(shieldHit.Absorbed * recoveryProfile.MakeshiftShare, recoveryProfile.MakeshiftDuration, at); }
                            if (shieldHit.Broken) { ShieldBroken(at); }
                            AdvanceEnemy(at);
                            continue;
                        }
                        // The hit's statuses go on with it, ahead of its damaged and armor_break events, so rules on those see them.
                        PutStatusesOn(at, FiringProfile());
                        if ((down is null ? TakeDamage(shooter.DamagePerHit, at, DummyEventKind.EnemyHit).Downed : TakeWhileDown(shooter.DamagePerHit, at, DummyEventKind.EnemyHit).Died)) { break; }
                    }
                    AdvanceEnemy(at);
                    continue;
                }
                // A swap or reload start waits for pickups, status ticks, whole seconds and skill damage at the same time, like a deferred reload start.
                if (nextAction < nextPickup && nextAction < nextStatusTick && nextAction < nextSecond && nextAction < nextSkillTime)
                {
                    Spend();
                    var (at, action, swapping, ending) = actionEvents.Dequeue();
                    if (at >= options.Duration) { continue; }
                    // Recovery runs up to the action at the rate in effect before it changes the hand or triggers.
                    if (!Regenerate(at)) { RefreshWeapon(at); }
                    if (player?.Downed == true) { continue; }
                    var refilled = false;
                    if (swapping)
                    {
                        var from = action.From;
                        hand = action.To;
                        headshots?.Reset();
                        // A waiting next-hit bonus comes down to the drawn weapon's cap, and stays down (R132).
                        nextHit?.Recap(bonusCaps![hand]);
                        HolsterTraps(at);
                        buffs!.Hold(at, weaponTags[hand]);
                        RefreshWeapon(at);
                        // Swap and swap_after_kill go to gear and the source weapon; swapped goes to gear and the drawn weapon.
                        var fromTalents = holdingTalents[from];
                        void EmitSwap(string name, string weapon, IReadOnlySet<string>? eligible = null)
                        {
                            FireStatuses(name, at, buffWeapon.StatusDurationFactor, eligible);
                            refilled |= FireBuffs(name, at, statuses!.Active(at), eligible, eligible, weapon);
                        }
                        EmitSwap("swap", from, fromTalents);
                        EmitSwap("swapped", hand);
                        if (lastWeaponKills.TryGetValue(from, out var lastKill) && at - lastKill <= SwapAfterKillWindow) { EmitSwap("swap_after_kill", from, fromTalents); }
                        // ADR 0033: a skill_out buff refilling on swaps (the Precision Smart Cover's), on now, fills the weapon put away,
                        // with no reload event, after every swap event has read it as put away. It never lowers its rounds.
                        if (swapRefillKeys.Length > 0 && buffs.Gates(at) is var swapGates && swapRefillKeys.Any(key => swapGates.Open(key, false))) { rotation!.RefillUp(from, at); }
                        // The draw spends the drawn weapon's primed rules, after its swapped rules read them; the weapon put away starts its time.
                        // After the swap's events: a lockout scheduling a gain at its end (Unwavering) reads the weapon it leaves as in hand.
                        buffs.Prime(at, holdingTalents[hand]);
                        // The swap's own time is read now, after its events: a bonus the swap ends speeds neither its holster nor its draw
                        // (the owner's reading of Unwavering, 2026-11-15). The weapon leaving is read as held, the drawn one as in hand. It only
                        // slows the swap; a bonus its own events gain applies from the next one.
                        RefreshWeapon(at);
                        var leaving = HeldProfile(from, at);
                        if (rotation!.RetimeSwap(action, leaving.SwapSpeed, leaving.SwitchMultiplier, buffWeapon.SwapSpeed, buffWeapon.SwitchMultiplier) is var reswapped &&
                            !ReferenceEquals(reswapped, action))
                        {
                            ReplaceAction(action, reswapped);
                            action = reswapped;
                        }
                    }
                    else
                    {
                        // A swap's reload on arrival is read as it begins, from the weapon in hand then (refreshed above), not as the
                        // decision to swap read it: the swap's events and what changed during it count, in either direction (R117).
                        if (!ending && action.Kind == WeaponActionKind.Swap && rotation!.RetimeArrival(action, buffWeapon.Reload) is var retimed &&
                            !ReferenceEquals(retimed, action))
                        {
                            ReplaceAction(action, retimed);
                            action = retimed;
                        }
                        var name = ending ? "reload_end" : "reload";
                        FireStatuses(name, at, buffWeapon.StatusDurationFactor);
                        refilled = FireBuffs(name, at, statuses!.Active(at), weapon: action.To);
                        // An empty magazine's reload is reload_empty too (R138), after reload: read from the rounds the reload started
                        // with, which a reload taking no time has already replaced in the readable rounds.
                        if (!ending && rotation!.ReloadStartRounds(action.To) < 1)
                        {
                            FireStatuses("reload_empty", at, buffWeapon.StatusDurationFactor);
                            refilled |= FireBuffs("reload_empty", at, statuses!.Active(at), weapon: action.To);
                        }
                    }
                    if (refilled) { Refill(at); }
                    RefreshWeapon(at);
                    // A reload starting is read again after its events, so what they turned on sizes it (ADR 0025).
                    if (!swapping && !ending && !refilled) { rotation!.ReloadStarting(action.To, at); RefreshWeapon(at); }
                    continue;
                }
                // A pickup lands after enemy attempts, status ticks and skill damage at the same time, and before a shot.
                if (nextPickup <= time && nextPickup < nextEnemy && nextPickup < nextStatusTick && nextPickup < nextSkillTime && nextPickup <= nextSecond)
                {
                    Spend();
                    var at = pickups.Dequeue();
                    if (!Regenerate(at)) { RefreshWeapon(at); }
                    if (player?.Downed == true) { continue; }
                    Record(DummyEventKind.Pickup, at: at, slot: "");
                    FireStatuses("pickup", at, buffWeapon.StatusDurationFactor);
                    if (FireBuffs("pickup", at, statuses!.Active(at))) { Refill(at); }
                    RefreshWeapon(at);
                    continue;
                }
                // A whole second passes after the fight's other events at that time, and before a swap, reload start or shot.
                if (nextSecond <= time && nextSecond < nextEnemy && nextSecond < nextStatusTick && nextSecond < nextSkillTime && nextSecond < nextPickup)
                {
                    Spend();
                    var at = nextSecond;
                    nextSecond = at + 1 < options.Duration ? at + 1 : double.PositiveInfinity;
                    if (!Regenerate(at))
                    {
                        if (targeted && target.Dead && at >= available)
                        {
                            target = NewTarget();
                            arrival = available;
                        }
                        RefreshWeapon(at);
                    }
                    if (player?.Downed == true) { continue; }
                    // A talent's drain (ADR 0026: Symbiosis) takes its share of a raised shield's maximum health, with its weapon in hand,
                    // before the second's rules; a drain that empties the shield breaks it as a hit would (owner, 2026-11-20).
                    if (drainUsed && shield!.IsUp && recoveryProfile!.ShieldDrainShare > 0)
                    {
                        FlushShieldRepair();
                        var drain = shield.Apply(recoveryProfile.ShieldDrainShare * shield.Capacity, at);
                        shieldDrained[fight] += drain.Absorbed;
                        if (!double.IsFinite(shieldDrained[fight])) { throw new SimulationException("damage-overflow", "Shield drain total overflowed"); }
                        Record(DummyEventKind.ShieldDrained, drain.Absorbed, at, shieldProfile!.Slot);
                        if (drain.Broken) { ShieldBroken(at); }
                    }
                    if (!secondsUsed) { continue; }
                    // A status applied on the second lands only on a living target; between targets there is none.
                    var applied = Spread(statuses!.Fire("second", at, buffWeapon.StatusDurationFactor, holdingTalents[hand], !targeted || !target.Dead,
                        gated ? buffs!.Gates(at) : null, target.Distance), at, holdingTalents[hand]);
                    var started = statuses.Started;
                    if (FireBuffs("second", at, statuses.Active(at))) { Refill(at); }
                    if (started) { FixNewStatus(); }
                    if (applied)
                    {
                        FireStatuses("status_applied", at, buffWeapon.StatusDurationFactor);
                        if (FireBuffs("status_applied", at, statuses.Active(at))) { Refill(at); }
                    }
                    RefreshWeapon(at);
                    continue;
                }
                // Enemy attempts win ties with status ticks, and skill damage at the same time lands first.
                if (nextStatusTick < options.Duration && nextStatusTick <= time && nextStatusTick < nextSkillTime && nextStatusTick <= nextPickup)
                {
                    Spend();
                    var at = nextStatusTick;
                    if (!Regenerate(at)) { RefreshWeapon(at); }
                    // An arrival that's due comes first, so a tick on the enemy arriving isn't a kill from afar.
                    if (targeted && target.Dead && at >= available)
                    {
                        target = NewTarget();
                        arrival = available;
                    }
                    var ticking = Ticking();
                    if (ticking?.Dots!.NextTick != at) { continue; }
                    var (status, tick, talentTick) = ticking.Dots!.Take(at);
                    if (targeted && ticking.Dead) { continue; }
                    var dealt = targeted ? ticking.Apply(tick, ArmorFactor(statusArmorBonuses.GetValueOrDefault(status, "")), 1) : tick;
                    if (talentTick) { talentTotal += dealt; }
                    else { statusTotal += dealt; }
                    total += dealt;
                    timeline[(int)at] += dealt / fights;
                    if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)at]))
                    { throw new SimulationException("damage-overflow", "Status damage overflowed"); }
                    Record(talentTick ? DummyEventKind.TalentTick : status switch
                    {
                        "burning" => DummyEventKind.BurnTick,
                        "bleeding" => DummyEventKind.BleedTick,
                        "poisoned" => DummyEventKind.PoisonTick,
                        _ => throw new SimulationException("status-damage-invalid", $"Status {status} has no tick event")
                    }, dealt, at, "");
                    if (targeted && ticking.Dead) { KillElsewhere(ticking, at); }
                    RefreshWeapon(at);
                    continue;
                }
                if (nextSkill is not null && (nextSkill.Time <= time || time >= options.Duration) && nextSkill.Time <= nextPickup)
                {
                    // A read, with nothing to deal, so it costs no work and moves nothing.
                    if (nextSkill.Kind == SkillDamageKind.Deployment)
                    {
                        // A downed player uses no skills: it waits for the revive (ADR 0037).
                        if (down is not null)
                        {
                            skills!.Postpone(down.ReviveAt);
                            continue;
                        }
                        // A skill waiting for armor reads it as regeneration has brought it by now (ADR 0030).
                        if (armorWaits.Count > 0) { _ = Regenerate(nextSkill.Time); }
                        skills!.Deploy();
                        // ADR 0032: a skill whose use holds a buff turns it on as it deploys, held until the use's window ends. A new use
                        // replaces the last one's hold, as a redeployed skill replaces the one out. A player downed as it deploys gains
                        // none for that use.
                        if (buffs is not null && skills.LastDeployed >= 0 && nextSkill.Time < options.Duration && player?.Downed != true &&
                            holdingSkills.Contains(skills.Plans[skills.LastDeployed].Skill) && skills.RunningUntil(skills.LastDeployed, nextSkill.Time) is var until && !double.IsNaN(until))
                        {
                            var holder = skills.Plans[skills.LastDeployed].Skill;
                            var owner = new HashSet<string>(StringComparer.Ordinal) { holder };
                            if (FireBuffs("skill_out", nextSkill.Time, statuses!.Active(nextSkill.Time), owner, owner)) { Refill(nextSkill.Time); }
                            buffs.HoldUntil(holder, nextSkill.Time, until);
                            RefreshWeapon(nextSkill.Time);
                        }
                        // ADR 0036: a use marking zones marks the enemy being shot, replacing the last mark; with none, it marks nobody.
                        if (skills.LastDeployed >= 0 && skills.Plans[skills.LastDeployed].MarksZones && nextSkill.Time < options.Duration && player?.Downed != true)
                        {
                            var marking = skills.LastDeployed;
                            Regenerate(nextSkill.Time);
                            if (targeted && target.Dead && nextSkill.Time >= available)
                            {
                                target = NewTarget();
                                arrival = available;
                            }
                            if (!targeted || !target.Dead)
                            {
                                var marks = skills.LastPlan(marking) ?? skills.Plans[marking];
                                zoneMarks[marking] = new(target, skills.LastUse(marking), marks.ZoneFactor) { Left = marks.ZoneHealth };
                            }
                            RefreshWeapon(nextSkill.Time);
                            // A skill held for the mark's key may be used now.
                            if (zoneKey.Length > 0) { skills.Release(nextSkill.Time); }
                        }
                        continue;
                    }
                    Spend();
                    var entry = skills!.Take();
                    // ADR 0031 stage 2c: a pulse with nothing to do (the hive out, the player down, or the fight over) does no fight work.
                    if (entry.Kind == SkillDamageKind.Pulse && (buffs is null || player?.Downed == true || entry.Time >= options.Duration || skills.Out(skills.IndexOf(entry.Slot), entry.Time)))
                    { continue; }
                    var skillStateRefreshed = Regenerate(entry.Time);
                    if (entry.Time < options.Duration && !skillStateRefreshed) { RefreshWeapon(entry.Time); }
                    // ADR 0030: an impact's repair landing after it, to the player's armor, capped, whatever became of the enemy.
                    if (entry.Kind == SkillDamageKind.Repair)
                    {
                        if (player is not null && entry.Time < options.Duration)
                        {
                            var repaired = player.Repair(entry.Damage, 0, entry.Time);
                            RecordRepair(repaired.Armor, repaired.Health, entry.Time, skills.Taken!.RepairEvery > 0 ? entry.Slot : "");
                        }
                        continue;
                    }
                    // ADR 0031: a buff hive's charge landing fires skill_charge, which only the skill's own rules hear.
                    // ADR 0031 stage 2c: a holstered hive's pulse sends a charge to each running use that may take one, spending none, each
                    // landing its lag later, the target taking the next only as long after that. A hive that's out doesn't pulse.
                    if (entry.Kind == SkillDamageKind.Pulse)
                    {
                        var index = skills.IndexOf(entry.Slot);
                        // The hive as the state now gives it, as a deployment reads it, so its lag and refresh follow the fight.
                        SkillDamageSchedule.Plan? pulsing = null;
                        for (var other = 0; other < skills.Plans.Length; other++)
                        {
                            if (other == index || !skills.Plans[other].Reachable || double.IsNaN(skills.RunningUntil(other, entry.Time)) ||
                                entry.Time < targetReady.GetValueOrDefault((other, skills.LastUse(other)), double.NegativeInfinity)) { continue; }
                            pulsing ??= skills.Read?.Invoke(entry.Slot, entry.Time) ?? skills.Taken!;
                            // A landing falling on a pulse but for rounding is that pulse's time.
                            var lands = entry.Time + pulsing.HolsteredLag;
                            var onGrid = Math.Round(lands / pulsing.HolsteredEvery) * pulsing.HolsteredEvery;
                            if (Math.Abs(onGrid - lands) < 1e-9) { lands = onGrid; }
                            if (lands < options.Duration)
                            {
                                if (!inFlight.TryGetValue((entry.Slot, lands), out var launched)) { inFlight[(entry.Slot, lands)] = launched = new Queue<int>(); }
                                launched.Enqueue(other);
                                skills.Schedule(index, pulsing, entry.Time, entry.Time, [new(lands, 0, entry.Slot, SkillDamageKind.Charge, lands)]);
                            }
                            targetReady[(other, skills.LastUse(other))] = lands + pulsing.HolsteredLag;
                        }
                        continue;
                    }
                    // ADR 0031: one charging skills lands on its target's use, if it still runs: the hive's buff, and the use lengthened by
                    // the hive's refresh (not for a chem launcher, the player's own: what that refreshes isn't known).
                    if (entry.Kind == SkillDamageKind.Charge)
                    {
                        var hive = skills.Taken!;
                        if (!hive.ChargeSkills)
                        {
                            if (entry.Time < options.Duration && player?.Downed != true) { FireCharge(hive.Skill, entry.Time); }
                            continue;
                        }
                        var landing = inFlight[(entry.Slot, entry.Time)];
                        var reached = landing.Dequeue();
                        if (landing.Count == 0) { inFlight.Remove((entry.Slot, entry.Time)); }
                        if (entry.Time < options.Duration && player?.Downed != true && !double.IsNaN(skills.RunningUntil(reached, entry.Time)))
                        {
                            FireCharge(hive.Skill, entry.Time);
                            if (skills.Plans[reached].Family != "chem_launcher")
                            {
                                skills.Lengthen(reached, entry.Time, hive.Refresh);
                                // ADR 0032: a buff the use holds lasts to its new end.
                                if (holdingSkills.Contains(skills.Plans[reached].Skill) && !double.IsNaN(skills.RunningUntil(reached, entry.Time)))
                                {
                                    buffs!.HoldUntil(skills.Plans[reached].Skill, entry.Time, skills.RunningUntil(reached, entry.Time));
                                    RefreshWeapon(entry.Time);
                                }
                            }
                        }
                        continue;
                    }
                    // ADR 0030: a repair skill's check. With the player's armor below its share, the last repair landed and its cloud over,
                    // it repairs, at once or landing later, or starts a cloud; each spends one of its count, and with all spent the use ends.
                    // ADR 0031: a buff hive's check fires a charge, with no armor test, the next waiting for the last one's buff to end.
                    if (entry.Kind == SkillDamageKind.RepairCheck)
                    {
                        var repairPlan = skills.Taken!;
                        if (!repairUses.TryGetValue((entry.Slot, entry.Start), out var repairUse))
                        { repairUses[(entry.Slot, entry.Start)] = repairUse = new RepairUse { Remaining = repairPlan.RepairCount }; }
                        var charging = repairPlan.ChargeFor > 0;
                        // ADR 0031: a hive charging skills sends one to each other slot's running use that may take one, while it holds any.
                        if (repairPlan.ChargeSkills)
                        {
                            if (buffs is null || player?.Downed == true || entry.Time >= options.Duration) { continue; }
                            var index = skills.IndexOf(entry.Slot);
                            double OnGrid(double at)
                            {
                                var check = entry.Start + Math.Round((at - entry.Start) / repairPlan.RepairEvery) * repairPlan.RepairEvery;
                                return Math.Abs(check - at) < 1e-9 ? check : at;
                            }
                            for (var other = 0; other < skills.Plans.Length && repairUse.Remaining > 0; other++)
                            {
                                if (other == index || !skills.Plans[other].Reachable || double.IsNaN(skills.RunningUntil(other, entry.Time)) ||
                                    entry.Time < targetReady.GetValueOrDefault((other, skills.LastUse(other)), double.NegativeInfinity)) { continue; }
                                var lands = OnGrid(entry.Time + repairPlan.RepairLag);
                                if (lands < options.Duration)
                                {
                                    if (!inFlight.TryGetValue((entry.Slot, lands), out var launched)) { inFlight[(entry.Slot, lands)] = launched = new Queue<int>(); }
                                    launched.Enqueue(other);
                                    skills.Schedule(index, repairPlan, entry.Start, entry.Time, [new(lands, 0, entry.Slot, SkillDamageKind.Charge, lands)]);
                                }
                                var ready = OnGrid(lands + repairPlan.ChargeFor);
                                targetReady[(other, skills.LastUse(other))] = ready;
                                repairUse.NextReady = Math.Max(repairUse.NextReady, ready);
                                if (--repairUse.Remaining <= 0) { skills.EndEarly(index, entry.Start, repairUse.NextReady + repairPlan.EndDelay); }
                            }
                            continue;
                        }
                        if (charging && buffs is not null && player?.Downed != true && entry.Time < options.Duration && repairUse.Remaining > 0 && entry.Time >= repairUse.NextReady)
                        {
                            var index = skills.IndexOf(entry.Slot);
                            // As for repairs, a time falling on a check but for rounding is that check's.
                            double OnGrid(double at)
                            {
                                var check = entry.Start + Math.Round((at - entry.Start) / repairPlan.RepairEvery) * repairPlan.RepairEvery;
                                return Math.Abs(check - at) < 1e-9 ? check : at;
                            }
                            var lands = OnGrid(entry.Time + repairPlan.RepairLag);
                            if (repairPlan.RepairLag == 0) { FireCharge(repairPlan.Skill, entry.Time); }
                            else if (lands < options.Duration) { skills.Schedule(index, repairPlan, entry.Start, entry.Time, [new(lands, 0, entry.Slot, SkillDamageKind.Charge, lands)]); }
                            repairUse.NextReady = OnGrid(lands + chargeBuffSeconds.GetValueOrDefault(repairPlan.Skill, repairPlan.ChargeFor));
                            if (--repairUse.Remaining <= 0) { skills.EndEarly(index, entry.Start, repairUse.NextReady + repairPlan.EndDelay); }
                        }
                        else if (!charging && player is not null && !player.Downed && entry.Time < options.Duration && repairUse.Remaining > 0 && entry.Time >= repairUse.NextReady &&
                            player.ArmorFraction < repairPlan.RepairBelow)
                        {
                            var index = skills.IndexOf(entry.Slot);
                            // A live repair reads its amount as the state now gives it (ADR 0031: the Artificer's bonus reaching a Fixer out).
                            var amount = repairPlan.LiveRepair && skills.Read is { } read ? read(entry.Slot, entry.Time).Repair : repairPlan.Repair;
                            // A time falling on one of the use's checks, but for rounding, is that check's exactly, so the check sees what
                            // lands then (a repair comes first at a moment).
                            double OnChecks(double at)
                            {
                                var check = entry.Start + Math.Round((at - entry.Start) / repairPlan.RepairEvery) * repairPlan.RepairEvery;
                                return Math.Abs(check - at) < 1e-9 ? check : at;
                            }
                            var lands = OnChecks(entry.Time + repairPlan.RepairLag);
                            var over = OnChecks(lands + repairPlan.RepairCloud);
                            if (repairPlan.RepairCloud > 0)
                            {
                                // Half the rate every 0.5 s, from 0.5 s after it lands to its end, whatever the armor.
                                var ticks = new List<SkillDamageEvent>();
                                for (var tick = 1; tick <= Math.Floor(repairPlan.RepairCloud * 2 + 1e-9) && OnChecks(lands + tick * 0.5) < options.Duration; tick++)
                                {
                                    var at = OnChecks(lands + tick * 0.5);
                                    ticks.Add(new(at, amount * 0.5, entry.Slot, SkillDamageKind.Repair, at));
                                }
                                skills.Schedule(index, repairPlan, entry.Start, entry.Time, ticks);
                            }
                            else if (repairPlan.RepairLag == 0)
                            {
                                var repaired = player.Repair(amount, 0, entry.Time);
                                RecordRepair(repaired.Armor, repaired.Health, entry.Time, entry.Slot);
                            }
                            else if (lands < options.Duration) { skills.Schedule(index, repairPlan, entry.Start, entry.Time, [new(lands, amount, entry.Slot, SkillDamageKind.Repair, lands)]); }
                            repairUse.NextReady = over;
                            if (--repairUse.Remaining <= 0) { skills.EndEarly(index, entry.Start, over + repairPlan.EndDelay); }
                        }
                        continue;
                    }
                    if (targeted && target.Dead)
                    {
                        if (entry.Time < available)
                        {
                            // ADR 0035: a pulse measured from the player lands in the gap on the living enemies within its reach.
                            if (entry.Kind == SkillDamageKind.Control && skills.Taken!.AreaFromPlayer)
                            {
                                GapControlUse(skills.Taken, entry.Time);
                                if (entry.Time < options.Duration) { RefreshWeapon(entry.Time); }
                            }
                            continue;
                        }
                        target = NewTarget();
                        arrival = available;
                    }
                    if (entry.Kind == SkillDamageKind.Control)
                    {
                        ControlUse(skills.Taken!, entry.Time);
                        if (entry.Time < options.Duration) { RefreshWeapon(entry.Time); }
                        continue;
                    }
                    // Against mixed targets, a skill deals its plan's own amount against the target in front of it, computed as the
                    // event's own is (an impact, or the rate over the batch), so an exact kill stays exact. A live rate reads the state now.
                    var rawSkillDamage = entry.Damage;
                    var skillPlan = skills.Taken!;
                    var liveRate = skillPlan.LiveRate && entry.Kind == SkillDamageKind.ContinuousWindow;
                    if (liveRate) { skillPlan = SkillsAt(entry.Time).For(entry.Slot); }
                    var (impact, rate, statusRate) = target.Profile >= 0 ? skillPlan.Amounts![target.Profile] : (skillPlan.Impact, skillPlan.Rate, skillPlan.StatusRate);
                    if (liveRate || target.Profile >= 0)
                    { rawSkillDamage = entry.Kind == SkillDamageKind.Impact ? impact : (entry.Kind == SkillDamageKind.StatusDot ? statusRate : rate) * (entry.Time - entry.Start); }
                    if (targeted && entry.Kind.IsBatch() && entry.Start < arrival)
                    { rawSkillDamage *= Math.Clamp((entry.Time - arrival) / (entry.Time - entry.Start), 0, 1); }
                    var skillStatusApplied = false;
                    if (entry.Kind == SkillDamageKind.StatusDot)
                    {
                        // The use applies it up to the end of its window, to an enemy there by then.
                        var from = targeted ? Math.Max(entry.Start, arrival) : entry.Start;
                        rawSkillDamage = from < entry.Time && !(targeted && target.Dead)
                            ? skillStatuses!.DealSkillStatus(skillPlan.Status, statusRate, from, entry.Time, skills.TakenUntil, from <= skills.TakenWindowEnd, out skillStatusApplied) : 0;
                    }
                    var skillArmorFactor = entry.Kind switch
                    {
                        SkillDamageKind.ContinuousWindow => ArmorFactor(skillSchedule!.RateArmorBonus(entry.Slot)),
                        SkillDamageKind.StatusDot => ArmorFactor(skillPlan.StatusArmorBonus),
                        _ => 1
                    };
                    // An armor-only impact (ADR 0029) stops at the enemy's armor.
                    var dealt = targeted ? target.Apply(rawSkillDamage, skillArmorFactor, entry.Kind == SkillDamageKind.Impact && skillPlan.ArmorOnly ? 0 : 1) : rawSkillDamage;
                    skillTotal += dealt;
                    switch (entry.Kind)
                    {
                        case SkillDamageKind.Impact: skillImpactTotal += dealt; break;
                        case SkillDamageKind.ContinuousWindow or SkillDamageKind.StatusDot: skillContinuousTotal += dealt; break;
                        default: throw new SimulationException("skill-event-kind-unsupported", "Unknown skill damage kinds are not modeled");
                    }
                    total += dealt;
                    var binTime = entry.Kind == SkillDamageKind.Impact ? entry.Time : Math.BitDecrement(entry.Time);
                    timeline[(int)binTime] += dealt / fights;
                    if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)binTime]))
                    { throw new SimulationException("damage-overflow", "Skill fight damage overflowed"); }
                    // A status waiting on a talent's, or that found nothing to land on, isn't a tick.
                    if (entry.Time < options.Duration && (rawSkillDamage > 0 || entry.Kind != SkillDamageKind.StatusDot)) { Record(entry.Kind == SkillDamageKind.Impact ? DummyEventKind.SkillImpact : DummyEventKind.SkillTick, dealt, entry.Time, entry.Slot); }
                    if (entry.Kind == SkillDamageKind.Impact && targeted)
                    {
                        AreaImpact(skillPlan, entry.Slot, entry.Time);
                        TargetsImpact(skillPlan, entry.Slot, entry.Time);
                        KillRolls(skillPlan, entry.Slot, entry.Time);
                    }
                    // ADR 0028: a landed impact's repair goes to the player's armor, capped, whether or not it's needed (the Leech Hive). One
                    // landing later is an event of its own (ADR 0030).
                    if (entry.Kind == SkillDamageKind.Impact && skillPlan.ImpactRepair > 0 && skillPlan.RepairDelay == 0 && player is not null && entry.Time < options.Duration)
                    {
                        var repaired = player.Repair(skillPlan.ImpactRepair, 0, entry.Time);
                        RecordRepair(repaired.Armor, repaired.Health, entry.Time);
                    }
                    // A cloud reaches the others in its radius while it applies, as each piece of the window ends (ADR 0024).
                    if (entry.Kind == SkillDamageKind.StatusDot && targeted && entry.Start < skills.TakenWindowEnd) { AreaStatus(skillPlan, entry.Time, skills.TakenWindowEnd); }
                    if (targeted && target.Dead)
                    {
                        CreditKill(entry.Time, entry.Slot, () => RepairOnKill(entry.Time));
                        if (options.Rotation?.Mode == WeaponSwapMode.AfterKill) { externalKillPending = true; }
                    }
                    if (buffs is not null && entry.Time < options.Duration)
                    {
                        var skillRefill = false;
                        void EmitSkillEvent(string name, IReadOnlySet<string>? eligible = null)
                        {
                            FireStatuses(name, entry.Time, buffWeapon.StatusDurationFactor, eligible);
                            skillRefill |= FireBuffs(name, entry.Time, statuses!.Active(entry.Time), eligible);
                        }
                        // A burn's tick is no hit, as a talent's isn't.
                        if (dealt > 0 && entry.Kind != SkillDamageKind.StatusDot && skillHitGroups.Add(entry.HitGroup)) { EmitSkillEvent("skill_hit"); }
                        // A skill's burn or bleed is the same status a talent's is (R61), so going on it counts as an application. A rate
                        // with a status only carries it, as Stinger Hive's stings do; a status's own damage is its statusRate (R68).
                        if (dealt > 0 && entry.Kind == SkillDamageKind.ContinuousWindow && skills.Taken!.Status.Length > 0 && skills.Taken.StatusRate == 0 && !(targeted && target.Dead))
                        { skillStatusApplied = statuses!.ApplyFromSkill(skills.Taken.Status, skills.TakenUntil, entry.Start, entry.Time, rateIsStatus: false); }
                        // Each application notes how long one lasts there, newly on or not, so a pass-on can read it later (ADR 0015).
                        if (skillPlan.Status.Length > 0 && !(targeted && target.Dead)) { statuses?.NoteSkillSpan(skillPlan.Status, skillPlan.StatusWindow, entry.Time); }
                        if (skillStatusApplied && !(targeted && target.Dead))
                        {
                            EmitSkillEvent("status_applied");
                            // The talents' own reactions spread first, as before; the skill's status then takes what the cooldown leaves.
                            SkillStatusOn(skillPlan.Status, entry.Time);
                        }
                        // A skill's kill, its own burn or bleed included, is a kill to gear talents. It's a skill_kill to them and to the weapon
                        // in hand's talents, so a weapon talent that takes kills from any source names it (R84: Outsider, Ignited and
                        // Preservation); a holstered weapon's talents aren't active.
                        if (targeted && target.Dead)
                        {
                            EmitSkillEvent("kill", gearTalents);
                            EmitSkillEvent("skill_kill");
                        }
                        if (skillRefill) { Refill(entry.Time); }
                    }
                    if (targeted && target.Dead) { ClearTarget(entry.Time); }
                    if (entry.Time < options.Duration) { RefreshWeapon(entry.Time); }
                    continue;
                }
                if (time >= options.Duration) { break; }
                Regenerate(time);
                // The kit's repair lands as its use ends.
                if (kits?.Ends is not null)
                {
                    var repair = player!.Repair(player.MaxArmor * kits.Finish(), 0, time);
                    kitsUsed++;
                    kitRepaired += repair.Armor;
                    RecordRepair(repair.Armor, 0, time);
                    Record(DummyEventKind.ArmorKitUsed, repair.Armor, time, "");
                    if (targeted && target.Dead && time >= available)
                    {
                        target = NewTarget();
                        arrival = available;
                    }
                    // A use cancelled by going down never gets here, so no talent reacts to it.
                    FirePlayerEvent("armor_kit", time);
                    RefreshWeapon(time);
                    continue;
                }
                // With nobody left the weapon starts nothing new; a reload under way completes.
                if (noTargetsLeft && !reloadCompleting)
                {
                    reloadStarting = false;
                    time = double.PositiveInfinity;
                    continue;
                }
                if (reloadStarting)
                {
                    reloadStarting = false;
                    var restarting = reloadRestarting && ammo < filled;
                    reloadRestarting = false;
                    if (ammo >= 1 && !restarting) { continue; }
                    reloadCompleting = true;
                    time = StartReload();
                    continue;
                }
                if (reloadCompleting)
                {
                    reloadCompleting = false;
                    Fill(time);
                    RefreshWeapon(time);
                    Record(DummyEventKind.ReloadCompleted);
                    FinishReload(time);
                    continue;
                }
                // A kit starts at the weapon's next decision, if armor is still low then, so a reload or swap under way finishes
                // first. With a cover cycle it waits for cover: the weapon's clock stops at each boundary, so a decision comes there.
                // A kit ability is instant, so it doesn't wait for cover: used when ready, and while it cools an instant kit from those carried.
                if (kitAbility is not null && !player!.Downed && player.ArmorFraction * 100 < options.ArmorKitAtPercent!.Value)
                {
                    if (time >= abilityReady)
                    {
                        abilityReady = time + abilityCooldown;
                        abilityUses++;
                        Record(DummyEventKind.KitAbilityUsed, slot: "");
                        FirePlayerEvent("kit_ability", time);
                        RefreshWeapon(time);
                        continue;
                    }
                    if (instantKit is not null && instantLeft > 0 && time >= instantReady)
                    {
                        instantLeft--;
                        instantReady = time + instantKit.Cooldown;
                        var repair = player.Repair(player.MaxArmor * instantKit.Repair, 0, time);
                        kitsUsed++;
                        kitRepaired += repair.Armor;
                        RecordRepair(repair.Armor, 0, time);
                        Record(DummyEventKind.ArmorKitUsed, repair.Armor, time, "");
                        FirePlayerEvent("armor_kit", time);
                        RefreshWeapon(time);
                        continue;
                    }
                }
                if (kits?.Wanted(player!.ArmorFraction) == true && (cycle is null || cycle.Behind(time)))
                {
                    Record(DummyEventKind.ArmorKitStarted, slot: "");
                    time = kits.Start(time);
                    continue;
                }
                Spend();
                // At each decision point a raised shield forces its allowed weapon, and after a break the build's weapon
                // returns. Neither interrupts a reload or swap already under way.
                if (shieldWeapon is not null && rotation is not null)
                {
                    var force = shield!.IsUp ? hand != shieldWeapon : shieldReturn && hand != active;
                    shieldReturn = false;
                    if (force)
                    {
                        // The forced swap also settles a pending kill swap, which would otherwise undo it.
                        externalKillPending = false;
                        if (time < options.Duration) { shieldSwitches++; }
                        ScheduleAction(rotation.ForceSwap(time));
                        continue;
                    }
                }
                if (externalKillPending && rotation is not null)
                {
                    externalKillPending = false;
                    if (rotation.SwapAfterExternalKill(time, SwapAllowed()) is { } action)
                    { ScheduleAction(action); continue; }
                }
                // A move waits for this decision, so a reload finishes first, and behind cover for the next peek.
                if (time >= moveAt && !target.Dead && group!.Alive.Count > 1 && cycle?.Behind(time) != true) { MoveOn(time); }
                if (targeted && target.Dead && time >= available)
                {
                    target = NewTarget();
                    arrival = available;
                }
                var coverIndex = cycle?.Index(time) ?? 0;
                var behindCover = cycle is not null && time >= cycle.CoverStart(coverIndex);
                if (behindCover)
                {
                    // No shot follows behind cover, so the state is brought up to date here (a reload that just completed).
                    RefreshWeapon(time);
                    // Entering cover reloads a magazine that isn't full, unless it's being kept for a swap when empty.
                    if (coveredCycle < coverIndex)
                    {
                        coveredCycle = coverIndex;
                        if (rotation is not null)
                        {
                            if (rotation.CoverReload(time, SwapAllowed()) is { } reload) { ScheduleAction(reload); continue; }
                        }
                        // Against the magazine as loaded: a state's larger magazine alone doesn't start a reload.
                        else if (!buffWeapon.NoReload && ammo < filled)
                        {
                            reloadCompleting = true;
                            time = StartReload();
                            continue;
                        }
                    }
                }
                // Every wait stops at the next cover boundary, so it can't sleep through a cover phase's reload.
                var nextBoundary = cycle is null ? double.PositiveInfinity : behindCover ? cycle.PeekStart(coverIndex + 1) : cycle.CoverStart(coverIndex);
                if (targeted && target.Dead) { time = Math.Min(available, nextBoundary); continue; }
                if (behindCover) { time = nextBoundary; continue; }
                if (cycle is not null && time < shotReady) { time = Math.Min(shotReady, nextBoundary); continue; }
                RefreshWeapon(time);
                if (buffs is not null && rotation is not null && hand != rotation.ActiveSlot)
                { throw new InvalidOperationException("A shot was due before its weapon's swap started"); }
                var weapon = buffs is null ? profiles[rotation?.ActiveSlot ?? active] : buffWeapon;
                var hitEvent = false;
                var critEvent = false;
                // Each critical pellet landing on a living target fires crit_pellet once (Clutch repairs per critical damage event;
                // a dead target's hits don't trigger it, R43).
                var critPellets = 0;
                // And each pellet landing on a living target fires hit_pellet once, for a talent counting pellets rather than shots.
                var hitPellets = 0;
                var headEvent = false;
                var bodyEvent = false;
                // Kill events follow the pellet that killed, not any pellet of the shot.
                var killingCrit = false;
                var killingHeadshot = false;
                // The killing pellet's damage as dealt, without a next-hit bonus it carried (ADR 0023).
                var killingRaw = 0d;
                var healthBefore = target.Health;
                var unarmoredBefore = !targeted || target.Armor == 0;
                double damage;
                var redirected = 0d;
                var criticalDamage = 0d;
                if (options.Mode == SimulationMode.Random)
                {
                    damage = 0;
                    var landed = 0;
                    // A ready forced-headshot rule (Resolved) makes this shot's first landed pellet a headshot; the shot's own
                    // event then spends it. The random draws are unchanged, so runs without such a rule keep their sequence. A rule
                    // may force a critical hit too, or every landed pellet of the shot (ADR 0023: Determined).
                    var forced = buffs?.Forcing(time, holdingTalents[hand], weaponTags[hand]);
                    var forceHead = forced is not null;
                    var forceCrit = forced?.Critical == true;
                    var forceWhole = forced?.WholeShot == true;
                    // ADR 0036: a body hit on an enemy with marked zones may land on one. Its draw comes only then, so other fights draw as before.
                    var mark = weapon.ZoneShare > 0 && zoneMarks.Count > 0 ? MarkOn(time) : null;
                    // A next-hit bonus waiting goes on the shot's first pellet to land on a living enemy (ADR 0023: Headhunter).
                    var nextHitBonus = nextHit is not null && (holdingTalents[hand]?.Contains(bonusRow!.Talent) ?? true) ? nextHit.Waiting(time) : 0;
                    for (var pellet = 0; pellet < (int)weapon.Pellets; pellet++)
                    {
                        Spend();
                        if (random.NextDouble() >= weapon.Hit) { continue; }
                        hitEvent = true;
                        landed++;
                        var critical = random.NextDouble() < weapon.Crit || forceCrit;
                        var headshot = random.NextDouble() < weapon.Head || forceHead;
                        if (!forceWhole) { forceHead = false; forceCrit = false; }
                        critEvent |= critical;
                        headEvent |= headshot;
                        bodyEvent |= !headshot;
                        var alive = !targeted || !target.Dead;
                        var zoned = !headshot && alive && mark is { Left: > 0 } && random.NextDouble() < weapon.ZoneShare;
                        var raw = (zoned ? weapon.ZoneDamage(critical, mark!.Factor)
                            : headshot ? critical ? weapon.CritHeadDamage : weapon.HeadDamage : critical ? weapon.CritDamage : weapon.BodyDamage) / weapon.Pellets;
                        var own = raw;
                        if (alive && nextHitBonus > 0)
                        {
                            raw += nextHitBonus;
                            nextHit!.Spend();
                            nextHitBonus = 0;
                        }
                        // The bonus is an added term, not critical damage, so critical repairs see only the pellet's own share.
                        var ownShare = raw > 0 ? own / raw : 1;
                        if (alive)
                        {
                            hitPellets++;
                            if (critical) { critPellets++; }
                        }
                        if (zoned) { zoneHits++; }
                        if (!targeted)
                        {
                            damage += raw;
                            if (zoned) { mark!.Left -= raw; }
                            continue;
                        }
                        // A redirect moves its share of the pellet to another enemy, which takes it as a fixed amount (Bewildered).
                        var redirect = alive ? Redirect(time) : null;
                        var dealt = target.Apply(redirect is { } moving ? raw * (1 - moving.Share) : raw, weapon.ArmorFactor, weapon.HealthFactor);
                        damage += dealt;
                        // A zone hit spends its zones' health by what it dealt the enemy.
                        if (zoned) { mark!.Left -= dealt; }
                        if (critical) { criticalDamage += dealt * ownShare; }
                        if (alive && target.Dead) { killingCrit = critical; killingHeadshot = headshot; killingRaw = own; }
                        if (redirect is { } moved)
                        {
                            // As the hit dealt it, with the weapon's armor and health factors, but none of the receiver's own.
                            var share = moved.Enemy.Apply(raw * moved.Share, weapon.ArmorFactor, weapon.HealthFactor);
                            redirected += share;
                            Record(DummyEventKind.DamageRedirected, share, time);
                            if (moved.Enemy.Dead) { blastKills.Enqueue((moved.Enemy, time)); }
                        }
                    }
                    target.PelletHits[hand] = landed;
                }
                else
                {
                    // ADR 0036: the mean of a marked enemy's zone hits, and what they spend, until a shot spends the last.
                    var expectedShot = weapon.Expected;
                    var mark = weapon.ZoneShare > 0 && zoneMarks.Count > 0 ? MarkOn(time) : null;
                    var zonedDamage = 0d;
                    if (mark is { Left: > 0 })
                    {
                        var hits = weapon.Hit * (1 - weapon.Head) * weapon.ZoneShare;
                        zonedDamage = hits * ((1 - weapon.Crit) * weapon.ZoneDamage(false, mark.Factor) + weapon.Crit * weapon.ZoneDamage(true, mark.Factor));
                        expectedShot += zonedDamage - hits * ((1 - weapon.Crit) * weapon.BodyDamage + weapon.Crit * weapon.CritDamage);
                        zoneHits += hits * weapon.Pellets;
                    }
                    damage = targeted ? target.Apply(expectedShot, weapon.ArmorFactor, weapon.HealthFactor) : expectedShot;
                    if (zonedDamage > 0 && expectedShot > 0) { mark!.Left -= damage * zonedDamage / expectedShot; }
                }
                // A shot is unarmored when its damage reaches health, including the shot that strips the last armor.
                var unarmored = unarmoredBefore || target.Health < healthBefore;
                // What a redirect moved is still the weapon's damage, dealt to another enemy.
                total += damage + redirected;
                weaponTotal += damage + redirected;
                timeline[(int)time] += (damage + redirected) / fights;
                if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)time]))
                { throw new SimulationException("damage-overflow", "Simulation damage overflowed"); }
                shots++;
                Record(DummyEventKind.Shot, damage);
                if (hitEvent) { Tag(time); }
                // A talent's repair from damage (ADR 0026: Symbiosis): its share of what the shot dealt its target, with its weapon in
                // hand, goes to a raised shield at once. A redirect's share isn't a bullet's (R140), so it repairs nothing.
                if (shield is not null && damage > 0 && recoveryProfile!.ShieldDamageShare > 0)
                {
                    AdvanceShield(time, queuedRepairs.Rate);
                    if (shield.IsUp)
                    {
                        FlushShieldRepair();
                        var repaired = shield.Repair(Math.Min(double.MaxValue, damage * recoveryProfile.ShieldDamageShare), time);
                        shieldRepairs[fight] += repaired;
                        if (!double.IsFinite(shieldRepairs[fight])) { throw new SimulationException("repair-overflow", "Shield recovery totals overflowed"); }
                        if (repaired > 0) { Record(DummyEventKind.ShieldRepaired, repaired, time, shieldProfile!.Slot); }
                    }
                }
                if (player is not null && criticalDamage > 0 && recoveryProfile!.CriticalDamageShare > 0)
                {
                    // Saturating an overflowing product is exact after the finite missing-armor cap.
                    var amount = Math.Min(double.MaxValue, criticalDamage * recoveryProfile.CriticalDamageShare);
                    var repair = player.Repair(amount, 0, time);
                    RecordRepair(repair.Armor, 0, time);
                }
                // A headshot kill arms the next-hit bonus from its killing pellet, capped for the weapon that made it, before the kill's
                // own handling can move anything on.
                if (killingHeadshot && nextHit is not null && (holdingTalents[hand]?.Contains(bonusRow!.Talent) ?? true)) { nextHit.Arm(killingRaw, bonusCaps![hand], time); }
                if (targeted && target.Dead)
                {
                    CreditKill(time, null, () => RepairOnKill(time));
                }
                var refill = false;
                if (buffs is not null)
                {
                    var applied = false;
                    var started = false;
                    // The source checks a gated status before the bullet's own gains (Ardent's filling bullet doesn't burn).
                    var bulletGates = gated ? buffs.Gates(time) : null;
                    void Emit(string name)
                    {
                        applied |= FireStatuses(name, time, weapon.StatusDurationFactor, gates: bulletGates);
                        started |= statuses!.Started;
                        refill |= FireBuffs(name, time, statuses!.Active(time), weapon: hand);
                    }
                    Emit("shot");
                    if (hitEvent) { Emit("hit"); if (unarmored) { Emit("unarmored_hit"); } }
                    for (var pellet = 0; hitPelletUsed && pellet < hitPellets; pellet++) { Emit("hit_pellet"); }
                    if (critEvent) { Emit("crit"); }
                    for (var pellet = 0; pellet < critPellets; pellet++) { Emit("crit_pellet"); }
                    if (headEvent) { Emit("headshot"); }
                    if (bodyEvent) { Emit("body_hit"); }
                    if (targeted && target.Dead)
                    {
                        Emit("kill");
                        if (killingCrit) { Emit("crit_kill"); }
                        if (killingHeadshot) { Emit("headshot_kill"); }
                    }
                    // A status landing on the bullet that kills dies with its enemy, so nothing reacts to it.
                    if (!(targeted && target.Dead))
                    {
                        if (started) { FixNewStatus(); }
                        if (applied) { Emit("status_applied"); }
                    }
                }
                // The headshot cycle (ADR 0021): any headshot uses a waiting bonus, after its damage; a shot with the cycle's weapon counts.
                if (headshots is not null)
                {
                    if (headEvent) { headshots.HeadshotLanded(); }
                    if (weaponTags[hand].Contains(cycleRow!.WeaponTag))
                    {
                        var steps = headshots.Shot(headEvent, killingHeadshot);
                        if (steps.Armor) { CycleArmor(time); }
                        refill |= steps.Refill;
                    }
                }
                if (targeted && target.Dead) { ClearTarget(time); }
                RefreshWeapon(time);
                if (noTargetsLeft) { time = double.PositiveInfinity; continue; }
                // A cover phase starting before the next shot stops the weapon's clock at its boundary; the shot still waits out
                // its fire interval, however short the cover phase. A swap or reload already started carries on.
                double? coverStart = null;
                if (cycle is not null)
                {
                    shotReady = time + weapon.Interval;
                    coverStart = cycle.CoverStart(cycle.Index(time));
                }
                // A shot's refill of both weapons fills the holstered one before the rotation decides, so it isn't drawn to be reloaded.
                if (refill) { RefillHolstered(time); }
                refillBoth = false;
                if (rotation is not null)
                {
                    ScheduleAction(rotation.Fire(time, targeted && target.Dead, refill, weapon.Interval, SwapAllowed(), coverStart));
                    continue;
                }
                if (!weapon.NoReload)
                {
                    if (refill) { Fill(time); } else { SetAmmo(ammo - 1, time); }
                }
                // Magazine-driven regeneration starts with the post-shot rounds, not the next shot.
                if (player is not null) { RefreshWeapon(time); }
                var next = time + weapon.Interval;
                if (next <= time) { throw new SimulationException("time-not-advancing", "The shot interval cannot advance time"); }
                if (coverStart < next) { next = coverStart.Value; }
                time = next;
                if (!weapon.NoReload && ammo < 1 && time < options.Duration)
                {
                    // Enemy attempts, status ticks, pickups and whole seconds may land before the reload starts, so it waits its turn.
                    if (incoming is not null || dotsUsed || pickupsUsed || secondsUsed) { reloadStarting = true; continue; }
                    time = StartReload();
                    Fill(time);
                    if (time < options.Duration) { Record(DummyEventKind.ReloadCompleted); }
                    // Nothing can land during this reload, so its completion fires at once.
                    FinishReload(time);
                }
            }
            Regenerate(options.Duration);
            FlushRegeneration();
            FlushShieldRepair();
            if (playerStatuses is not null)
            {
                playerStatuses.Close(player?.DownAt ?? options.Duration);
                foreach (var (name, seconds) in playerStatuses.Seconds) { playerStatusSeconds[name] = playerStatusSeconds.GetValueOrDefault(name) + seconds; }
            }
            if (statuses is not null)
            {
                foreach (var (name, seconds) in statuses.UptimeSeconds(player?.DownAt ?? options.Duration))
                { statusSeconds[name] = statusSeconds.GetValueOrDefault(name) + seconds; }
            }
            if (buffs is not null)
            {
                foreach (var (key, measurement) in buffs.Measure(player?.DownAt ?? options.Duration))
                {
                    var prior = triggerSeconds.GetValueOrDefault(key);
                    triggerSeconds[key] = new((prior?.Seconds ?? 0) + measurement.Seconds,
                        measurement.StackSeconds is { } stackTime ? (prior?.StackSeconds ?? 0) + stackTime : null);
                }
            }
            if (options.DriveSimpleBuffs)
            {
                AccumulatePhases(cycleSeconds, phases!.MeasureCycles(player?.DownAt ?? options.Duration, cancellationToken));
            }
            if (magazine is not null)
            {
                AccumulatePhases(magazineSeconds, magazine.Measure(player?.DownAt ?? options.Duration));
            }
            if (heldMagazine is not null)
            {
                AccumulatePhases(magazineSeconds, heldMagazine.Measure(player?.DownAt ?? options.Duration));
            }
            dps[fight] = weaponTotal / options.Duration;
            if (player?.FirstArmorBreak is { } firstBreak) { breakTimes.Add(firstBreak); }
            if (player?.DownAt is { } downAt)
            {
                downed++;
                downTimes.Add(downAt);
                if (kits?.Ends is not null) { kitsCancelled++; }
            }
            skillStatistics?.Record(fight, skillTotal, skillImpactTotal, skillContinuousTotal, options.Duration);
            if (statusDps is not null)
            {
                statusDps[fight] = statusTotal / options.Duration;
                if (!double.IsFinite(statusDps[fight])) { throw new SimulationException("damage-overflow", "Status DPS overflowed"); }
            }
            if (talentDps is not null)
            {
                talentDps[fight] = talentTotal / options.Duration;
                if (!double.IsFinite(talentDps[fight])) { throw new SimulationException("damage-overflow", "Talent damage DPS overflowed"); }
            }
            totalDps[fight] = total / options.Duration;
            if (timedKills > 0) { killTimeMeans.Add(fightKillSeconds / timedKills); }
            if (skills is not null)
            {
                foreach (var slot in skillUse.Keys)
                {
                    var index = skills.IndexOf(slot);
                    var (uses, held) = skillUseTotals[slot];
                    skillUseTotals[slot] = (uses + skills.UsesOf(index), held + skills.HeldFor(index, options.Duration));
                }
                foreach (var slot in armorWaitSlots.Keys) { armorWaitUses[slot] += skills.UsesOf(skills.IndexOf(slot)); }
            }
            if (!double.IsFinite(dps[fight]) || !double.IsFinite(totalDps[fight])) { throw new SimulationException("damage-overflow", "Simulation DPS overflowed"); }
            progress?.Report(new(fight + 1, fights));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Inputs the incoming-fire recovery model sets from actual events, which coverage otherwise reports as assumptions.
        ImmutableHashSet<string> RecoveryKeys()
        {
            var keys = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            if (options.IncludeIncoming && pack.Pack.NumericContextInputs.Any(input => input.ID == "makeshift_qualifying_damage")) { keys.Add("makeshift_qualifying_damage"); }
            return keys.ToImmutable();
        }
        return new DummyFightResult(options.IncludeIncoming ? "d2p-incoming-random-v75/" + SimulationRandom.Contract : options.DriveSimpleBuffs ? "d2p-simple-buffs-v62/" + SimulationRandom.Contract : options.IncludeSkills ? "d2p-static-skills-v6/" + SimulationRandom.Contract : template is not null ? "d2p-weapon-rotation-v1/" + SimulationRandom.Contract : targeted ? "d2p-static-target-v1/" + SimulationRandom.Contract : Contract, options.Mode, fights, options.Duration, options.Seed, Summarize(dps), shots / fights,
            reloads / fights, [.. timeline], [.. events.OrderBy(entry => entry.Time)], truncated, diagnostics,
            [options.IncludeIncoming ? "Incoming fire: random targets" + (template is not null ? " with driven weapon swaps (recovery follows the weapon in hand)" : "") + "; other queued repairs, shield swaps without a weapon rotation and triggered cadence changes are unsupported. " + (pack.Pack.Simulation.EnemyStatuses.IsDefaultOrEmpty ?
                "An enemy's hit deals its damage only: a status it puts on you, such as a nail gun's bleed, isn't counted" :
                "A landed hit puts the pack's statuses for its enemy's profile on you (ADR 0018), not one on a raised shield: a tick goes through bonus armor, armor then health as a hit does, without extending an armor kit's use, and the row's resistances shorten the duration, as the source most likely does. A control status on you has no effect yet") : targeted ? "Static targets: no incoming damage or repairs" : "Unlimited dummy: no target pools, kills, incoming damage or repairs",
             options.IncludeShield ? "One frontal shield stays raised with static health, active regeneration and break cooldown; holstering, flanking and manual deployment are excluded" +
                 (shieldWeapon is not null ? $". The shield allows only the {pack.Pack.Slots.FirstOrDefault(slot => slot.ID == shieldWeapon)?.Name ?? shieldWeapon}: a raised shield swaps to it at the next shot or after the current reload (the barred weapon may hold the shield until then), and after a break the build's weapon returns the same way" : "") : "Shield absorption is excluded",
             options.IncludeSkills ? "Skills read the build as each deploys, and a use keeps its amounts and cooldown unless its rate is live, with up to 100 ms continuous-damage batches" + (perTargetFire ? ", not split at enemy attempts, which follow the " + (options.EnemiesAtOnce is null ? "target in play" : "enemies in play") : " split at incoming attempts") + "; a use's impacts land together unless its rule spaces them, and one due with no enemy there is lost. In driven fights a skill's burn, bleed or poison reads as on its enemy until the use's window ends, or for one with its own rate until that runs out, with no share in target status uptime. A talent's status starts no ticks while a skill's rate is that status's damage, and ticks as usual after, and the skill's deals nothing while a talent's runs. A use reads a waiting bonus only if it was up as the use deployed; other skill statuses are not modeled" : options.IncludeShield ? "Damaging skills are excluded; the selected shield is simulated" : "Skills are excluded",
             options.DriveSimpleBuffs && template is not null ? "Selected triggers follow weapon swaps and whole seconds; swap_after_kill requires the source weapon's own kill within 10 seconds. A triggered fire rate paces the next interval; triggered reload times and swap speeds apply at each swap or reload decision, with the drawn weapon read as it would be in hand. Magazine readings follow the weapon in hand and are unset for a holstered owner, and a weapon's triggered magazine size applies from its next fill. A talent on both weapons runs one copy per weapon, reported as key@slot; with status rules or keys shared with another talent it is refused, and with holstered effects unless its rules are all shared. Copied talents with their own combat rules and refills before queued empty actions start are unsupported" :
             options.DriveSimpleBuffs ? "Selected triggers include whole-second, skill-hit and gear skill-kill events; a triggered fire rate or reload time applies from the next interval or reload, and a weapon's triggered magazine size from its next fill. Changing pellets, swaps and copied talents with their own combat rules are unsupported" : "Assumptions remain fixed; pack triggers, statuses and timers are not advanced",
             !dotRules.IsEmpty ? (statusArmorBonuses.IsEmpty ? "Burn, bleed and poison ticks deal the pack's status damage to target pools without armor or health factors" :
                 "Burn, bleed and poison ticks deal the pack's status damage to target pools, with the pack's armor bonus on armor and no health factor") + "; a status kill credits gear talents, generic kill recovery and target_death rules (Doctor Home's kit), not weapon repair-per-kill or weapon talents' kill rules (Preservation)" :
                 "Status damage over time is excluded unless the pack defines it for a status a selected talent applies",
             !talentDamageRules.IsEmpty ? "Talent debuffs (Toxic Delivery, and Afterburn's single tick) spread their resolved total over equal ticks to the current target's pools without armor or health factors; one runs per target and its event neither restarts nor stacks it, and a kill by one counts as a status kill" :
                 "Talent damage over time is excluded unless the pack defines it for a selected talent",
             options.IncludeIncoming ? "Clutch, Doctor Home and Preservation repair through their pack rules (R43), only when talent triggers are driven; overlapping timed repairs run as independent, uncapped instances" :
                 "Talent repairs from pack rules need incoming fire",
             "Shot-bonus and returned-round mechanics are rejected, and so is a magazine change beside a magazine-sized stack cap",
             .. incomingNotes,
             .. unreachedRepairs.Where(repair => unreachedSeen.Contains(repair.Talent)).Select(repair =>
                 $"{pack.Pack.Talents.FirstOrDefault(talent => talent.ID == repair.Talent)?.Name ?? repair.Talent}'s repair doesn't happen in this fight: {repair.Why}"),
             .. sharedCopyNotes,
             .. distanceNotes,
             .. (distances.IsEmpty ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"Targets cycle through {distances.Length} distances ({string.Join(", ", distances.Distinct().Select(meters => meters.ToString("0.##", CultureInfo.InvariantCulture)))} m), each setting its falloff, its distance rules and any enemy fire that follows distance")]),
             .. (cycleRow is null ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"{RuleOwnerName(pack.Pack, cycleRow.Talent)} counts consecutive headshots with weapons tagged {cycleRow.WeaponTag}: from {cycleRow.KillFrom} on only a headshot kill counts, any other shot with them is a miss ({cycleAllowance} forgiven a cycle), and a swap starts it again. The first sets up the next headshot's bonus, the second repairs {cycleRepair:0.##%} of max armor (at full armor, adds it as lasting bonus armor below {cycleCap:0.##%} of max armor), the third refills the magazine, and each after does all three") +
                 (incoming is null ? ". This fight has no enemy fire, so the armor step does nothing" : "")]),
             .. (bonusRow is null || bonusCaps is null || !targeted ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"{RuleOwnerName(pack.Pack, bonusRow.Talent)} adds {bonusRow.Share:0.##%} of a headshot kill's killing pellet, as dealt before the enemy's armor, to the next pellet that lands on a living enemy within {bonusRow.Window:0.##} s, up to {string.Join(", ", bonusCaps.Select(pair => $"{pair.Value:0.##} with {pair.Key}"))}; the cap is read as the fight starts, and a swap brings a waiting bonus down to the drawn weapon's")]),
             .. (kitAbility is null ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"{RuleOwnerName(pack.Pack, kitAbility.Talent)} is the build's armor kit: used at once, with no use time, if armor is below {options.ArmorKitAtPercent:0.##}% at the weapon's next decision, whenever its {abilityCooldown:0.##} s cooldown is ready") +
                 (instantKit is null ? ". Carried kits aren't used" : options.ArmorKits == 0 ? ". No kits are carried, so no instant kit is used" : string.Create(CultureInfo.InvariantCulture,
                 $". While it cools, an instant kit from the {options.ArmorKits} carried repairs {instantKit.Repair:0.##%} of max armor at once, each on a {instantKit.Cooldown:0.##} s cooldown, and isn't refilled. Neither waits for cover"))]),
             .. (armorKit is null || kitAbility is not null ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"Assumed armor kits: {options.ArmorKits} carried, each used if armor is below {options.ArmorKitAtPercent:0.##}% at the weapon's next decision") +
                 (cycle is null ? "" : ", waiting for cover, ahead of that cover phase's reload") + string.Create(CultureInfo.InvariantCulture,
                 $". A kit stops the weapon for {armorKit.UseSeconds:0.##} s, longer as hits land on armor (not on a shield), repairs {armorKit.Repair:0.##%} of max armor as it ends, and isn't refilled")]),
             .. (cycle is null ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"Assumed cover cycle: {cycle.Peek:0.##} s peeking from cover, then {cycle.Cover:0.##} s behind it ({cycle.BehindShare(options.Duration):0.#%} of this fight), starting with a peek. Behind cover the weapon doesn't fire") +
                 (options.Rotation?.Mode == WeaponSwapMode.WhenEmpty ? "" : " and a magazine that isn't full reloads") +
                 (options.IncludeIncoming ? string.Create(CultureInfo.InvariantCulture, $"; enemy fire is blocked there (counted as blocked shots), and while peeking enemies land {options.PeekHitShare:0.##%} of their normal hit chance") :
                     options.PeekHitShare is null ? "" : ". Enemy fire is off, so the hit chance while peeking isn't used") +
                 (template is null ? "" : ". Swaps and reloads under way carry on behind cover" +
                     (options.Rotation!.Mode == WeaponSwapMode.WhenEmpty ? ", and a magazine is kept for the swap when empty; only a weapon a raised shield holds in hand reloads there" : ", and only the weapon in hand reloads there")) +
                 (coverSelector ? ". In cover throughout, so in-cover effects apply to every shot" : "")]),
             .. (targeted ? ["Time to kill is each fight's average from a target's arrival to its death, including the fire interval or reload carried over from the last target; a target still alive at the end isn't counted, and fights without a kill are left out (see the killed share)"] : Array.Empty<string>()),
             .. (sequence.IsEmpty ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"Targets cycle through a sequence of {sequence.Length} (profiles {string.Join(", ", sequence.Distinct())}), each with its own pools and target-dependent bonuses, skill damage included") +
                 (options.EnemiesAtOnce is not null ? "; each enemy fires as its own profile, and the one being shot sets recovery" :
                     perTargetFire ? "; the target in play (the arriving one after a kill) sets enemy fire and recovery, and a kill scales the wait for the next attempt to the new cadence" : "")]),
             .. (firstDistance is null && statusRules.Any(rule => rule.Within > 0) ? ["With no fight distance, a status rule's range is taken as met"] : Array.Empty<string>()),
             .. (firstDistance is null && buffRules.Any(rule => rule.Within > 0) ? ["With no fight distance, a talent rule's range (a kill within so many metres) is taken as met"] : Array.Empty<string>()),
             .. (setSettings.IsEmpty ? Array.Empty<string>() : [$"This fight doesn't drive these gear set settings, so their bonuses stay as you set them: {string.Join("; ", setSettings)}"]),
             .. (kitlessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight carries no armor kits, so these read your setting and never react to one: {string.Join(", ", kitlessTalents)}"]),
             .. (unfiredBonusTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no enemy fire, so these read your setting for the player's bonus armor: {string.Join(", ", unfiredBonusTalents)}"]),
             .. (distancelessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no distance, so these read your setting, or the pack's default, for their range: {string.Join(", ", distancelessTalents)}"]),
             .. (swaplessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no weapon rotation, so these read your setting and never react to a swap: {string.Join(", ", swaplessTalents)}"]),
             .. (skilllessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no skills, so these never react to a skill's hit or kill: {string.Join(", ", skilllessTalents)}"]),
             .. (shieldlessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no shield, so these never react to one breaking: {string.Join(", ", shieldlessTalents)}"]),
             .. (!shieldTalent ? Array.Empty<string>() : shieldProfile is null ? ["This fight has no shield, so a talent's shield drain and repair from damage (Symbiosis) have nothing to act on"] :
                 ["A talent's shield drain and repair from damage (ADR 0026: Symbiosis) apply with its weapon in hand: the raised shield loses its share of maximum health on each whole second of the fight, which can break it, and gains its share of the damage each shot deals its target, redirects excluded"]),
             .. AsideNotes(pack.Pack, asideSkills, droppedUses),
             .. (!asideSkills.IsEmpty && skillSchedule!.IsEmpty && !skillEventTalents.IsEmpty
                 ? [$"No skill in this fight deals damage, so these never react to a skill's hit or kill: {string.Join(", ", skillEventTalents)}"] : Array.Empty<string>()),
             .. delayedSkills.Select(rule => $"{pack.Pack.ActiveSkills.FirstOrDefault(skill => skill.ID == rule.Skill)?.Name ?? rule.Skill}'s effects land {rule.ImpactDelay:G} s after its use" +
                 (rule.FarFrom <= 0 ? "" : FightDistance(options) is null ? $", or {rule.FarImpactDelay:G} s beyond {rule.FarFrom:G} m, which needs the fight's one enemy distance"
                     : $" ({rule.FarImpactDelay:G} s beyond {rule.FarFrom:G} m), by the fight's enemy distance")),
             .. killSkills.Select(rule => $"{pack.Pack.ActiveSkills.FirstOrDefault(skill => skill.ID == rule.Skill)?.Name ?? rule.Skill} can kill outright: a roll on each enemy its hit reaches, of profiles {string.Join(", ", rule.KillProfiles)} and below full health, killing {rule.KillDelay:G} s later; such a kill counts, but no kill talent hears it"),
             .. (armorOnlySkills ? ["A skill's armor-only hit (The Howl) stops at each enemy's armor and never reaches health"] : Array.Empty<string>()),
             .. (!repairSkills ? Array.Empty<string>() : options.IncludeIncoming
                 ? ["A skill's repair per impact (Leech Hive) goes to the player's armor at each impact, or a set time after it, capped, whether or not it's needed"]
                 : ["This fight has no enemy fire, so a skill's repair per impact (Leech Hive) has nothing to restore"]),
             .. (targetSkills ? ["A skill with several targets (Demolisher Firefly) strikes, as it's used, the enemy being shot and then the nearest other living enemies, up to its count, one full impact each to armor and health, as a body hit; the flight between them isn't modeled"] : Array.Empty<string>()),
             .. (skillFights?.Plans.FirstOrDefault(plan => plan.Revive is not null) is { Revive: { } reviveRule } revivePlan ? [string.Create(CultureInfo.InvariantCulture,
                 $"A skill reviving you ({SkillName(pack.Pack, revivePlan.Skill)}) does so once a use, if you go down while it's out; you're taken to be in its range. Until then you shoot and use nothing, and fire lands at {reviveRule.DamageTaken:0.##%} on a downed pool of {reviveRule.PoolShare:0.##%} of your health, which also bleeds out in {reviveRule.BleedSeconds:0.##} s; spent first, the fight ends. You come back at full health, and nothing hurts you for {reviveRule.ImmuneSeconds:0.##} s. Buffs and statuses run on, and a reload or armor kit under way stops. The game may take more from a downed player")] : Array.Empty<string>()),
             .. (zones is not null ? ["A skill marking zones (Achilles Pulse) marks the enemy being shot as it's used, for its window: the set share of the weapon's body hits on it land on a zone and take the headshot multiplier, added to critical damage, until the hits' damage has spent the zones' health (all zones as one pool). A zone hit stays a body hit for events and talents; skill hits, marked heads and allies play no part" +
                 (zoneKey.Length > 0 ? $". While it's marked, {pack.Pack.Selectors.FirstOrDefault(selector => selector.ID == zoneKey)?.Name ?? zoneKey} holds for the enemy being shot, and always if you've set it" : "")] : Array.Empty<string>()),
             .. (controlSkills ? ["Control skills put their status on the enemy being shot as they're used, for their window, and with an area on every enemy within it; a trap's goes on once per use, and no immunity is modeled"] : Array.Empty<string>()),
             // ADR 0035: an area measured from the player, said only where a selected rule has one, so other fights' notes don't move.
             .. (controlSkills && pack.Pack.Simulation.Skills.Any(rule => rule.AreaFromPlayer && rule.ControlStatus && selectedSkills.Contains(rule.Skill))
                 ? ["A pulse's area is measured from the player: each enemy is reached by its own distance (without a distance, along the line from the enemy being shot), the enemy being shot too, and other enemies only with several at once and a spacing; a pulse landing with no living enemy being shot reaches the living ones within its radius by their own distance, or nobody without a distance"] : Array.Empty<string>()),
             .. (reachActive && impactAreas ? ["Skills with an area land each impact on every enemy within its radius of the enemy being shot" + (areaFalls
                 ? ", falling in a straight line from a full-damage radius to the edge's share where the row gives one (an assumption: the source gives the ends, not the curve)" : ", at full damage") + ", with the burn, bleed or poison it puts on for one application; kills there are kills elsewhere, and hit and status events stay the enemy being shot's"] : Array.Empty<string>()),
             .. (reachActive && cloudAreas ? ["Skills whose burn, bleed or poison has an area (a cloud) put it on every enemy within its radius of the enemy being shot while they apply it, so each keeps it to the end plus one application; enemies don't walk out of a cloud, since fights don't move enemies"] : Array.Empty<string>()),
             .. (reachActive && !spreadRules.IsEmpty ? [spreadRules.Any(rule => rule.SkillStatuses)
                 ? "Spreads copy statuses talents put on the enemy being shot, and where a spread's row says so a skill's own burn, bleed or poison, for what's left of it up to one application, carrying the skill's own damage where its status has a rate of its own"
                 : "Spreads copy statuses talents put on the enemy being shot; a skill's own status doesn't spread"] : Array.Empty<string>()),
             .. (skillUse.IsEmpty ? Array.Empty<string>() : [$"Assumed skill use: {string.Join("; ", skillUse.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value}"))}. A held skill's cooldown starts when it's used, and others are used as soon as they're ready"]),
             .. (skillUseTotals.Where(pair => pair.Value.Uses == 0 && !armorWaitSlots.ContainsKey(pair.Key)).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } unused
                 ? [$"Never used, since its condition never held: {string.Join(", ", unused.Select(slot => $"{slot} {skillUse[slot]}"))}"] : Array.Empty<string>()),
             .. (skillFights is null ? [] : skillFights.Plans.Where(plan => plan.ChargeFor > 0 && !plan.ChargeSkills).Select(plan => SkillName(pack.Pack, plan.Skill)).Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } chargers
                 ? [$"Buff hives' charges turn on their buffs one at a time, needed or not, timed as their rules give them (the player in range): {string.Join(", ", chargers)}"] : Array.Empty<string>()),
             .. (skillFights is null ? [] : skillFights.Plans.Where(plan => plan.ChargeSkills).Select(plan => SkillName(pack.Pack, plan.Skill)).Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } skillChargers
                 ? [$"Hives charging skills reach the build's other skills while their uses run (in range), each charge turning on its buff and lengthening the use (not a chem launcher's); skill health isn't modelled: {string.Join(", ", skillChargers)}"] : Array.Empty<string>()),
             .. (skillFights is null ? [] : skillFights.Plans.Where(plan => plan.ChargeSkills).Select(plan => (plan.HolsteredEvery > 0, SkillName(pack.Pack, plan.Skill))).ToArray() is { Length: > 0 } holsters
                 ? [.. new[] { true, false }.Select(pulses => holsters.Where(entry => entry.Item1 == pulses).Select(entry => entry.Item2).Order(StringComparer.Ordinal).ToArray()).Zip(
                     ["Pulse while holstered, between their uses, as their rules give them: ", "Don't pulse while holstered, as their rules give none: "]).Where(pair => pair.First.Length > 0)
                     .Select(pair => pair.Second + string.Join(", ", pair.First))] : Array.Empty<string>()),
             .. (armorWaitSlots.Values.Where(plan => plan.RepairEvery > 0).Select(plan => SkillName(pack.Pack, plan.Skill)).Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } repairers
                 ? [$"Repair skills restore the player's armor only, capped, with the player assumed in range throughout: {string.Join(", ", repairers)}"] : Array.Empty<string>()),
             .. (armorWaitSlots.Count == 0 ? Array.Empty<string>() : [$"Used only once the player's armor is below a share: {string.Join("; ", armorWaitSlots.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{SkillName(pack.Pack, pair.Value.Skill)} below {SkillDamageSchedule.ArmorShareText(pair.Value.UseArmorBelow)}"))}"]),
             .. (armorWaitUses.Where(pair => pair.Value == 0).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } waiting
                 ? [$"Never used, since the player's armor never fell below its share{(waiting.Any(skillUse.ContainsKey) ? " while its own condition held" : "")}: {string.Join(", ", waiting.Select(slot => SkillName(pack.Pack, armorWaitSlots[slot].Skill)))}"] : Array.Empty<string>()),
             .. (trapActive ? ["Traps tag the enemies the player has hit for long enough, so under focus fire only the enemy being shot is tagged and a trap springs only when the player moves between enemies; the slow isn't modeled"] : Array.Empty<string>()),
             .. (!trapRules.IsEmpty && !trapActive ? [$"These traps find nobody, since the fight has no {(!targeted ? "targets" : "several enemies at once")}: {string.Join(", ", trapRules.Select(rule => RuleOwnerName(pack.Pack, rule.Talent)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}"] : Array.Empty<string>()),
             .. (reachActive && !redirectRules.IsEmpty ? ["Redirects move a share of each weapon hit to a random enemy in reach, as a fixed amount that's no hit and applies no status; fights have no armor plates, so every hit moves its share"] : Array.Empty<string>()),
             .. (reachActive && !passOnRules.IsEmpty ? ["Pass-ons move a talent's debuff stacks to the nearest enemy at a death, up to its stack cap, or put a share of its talents' statuses on the nearest or all in reach" + (passOnRules.Any(rule => rule.SkillStatuses)
                 ? ", and where a pass-on's row says so a skill's own burn, bleed or poison, for its share of one full application, carrying the skill's own damage where its status has a rate of its own" : "; a skill's own status doesn't pass on")] : Array.Empty<string>()),
             .. (options.EnemySpacing is { } spaced ? [string.Create(CultureInfo.InvariantCulture,
                 $"Assumed enemy spacing: {spaced:0.##} m between neighbours on a line, by their place in the group; an enemy joining takes the dead one's place")] : Array.Empty<string>()),
             .. (reachTalents.Length > 0 && !reachActive ? [$"These effects on other enemies find nobody, since the fight has no {(!targeted ? "targets" : options.EnemiesAtOnce is null ? "several enemies at once" : "enemy spacing")}: {string.Join(", ", reachTalents.Select(id => RuleOwnerName(pack.Pack, id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}"] : Array.Empty<string>()),
             .. (enemyCounts is null ? Array.Empty<string>() :
                 ["Talents that count enemies read those alive in the group, by each enemy's distance. Enemies don't move, so a count changes only as they die or join; one read on an event holds until that event comes round again, starting from none"]),
             .. (uncountedKeys.IsEmpty ? Array.Empty<string>() : [$"With one enemy at a time these stay as set: {string.Join(", ", uncountedKeys)}. Fights count enemies with several at once"]),
             .. (options.FocusSeconds is { } focusSeconds ? [string.Create(CultureInfo.InvariantCulture,
                 $"Assumed shooting pattern: {focusSeconds:0.##} s on an enemy, then on to the next in turn, taking {retargetDelay:0.##} s each move. Enemies keep what's on them, and burns, bleeds and poisons keep ticking; time to kill runs from first reaching an enemy, time away included")] : Array.Empty<string>()),
             .. (options.EnemiesAtOnce is { } enemyGroupSize ? [string.Create(CultureInfo.InvariantCulture,
                 $"{enemyGroupSize} enemies fire at once, staggered across an interval. Treat this as an upper bound: the game may limit how many fire at one player, and only an in-game count can settle it. You shoot one at a time; the others wait at full health and keep firing") +
                 (enemyTotal is { } enemiesInAll ? string.Create(CultureInfo.InvariantCulture, $". There are {enemiesInAll} in all: the next joins in a dead enemy's place until none are left, then fire falls with each kill, and after the last nobody fires or is shot for the rest of the fight") :
                     ", and the next joins in the dead enemy's place") + (options.FocusSeconds is null ? ". Only the enemy being shot can be shocked" : ". Only an enemy shot at can be shocked") + (!distances.IsEmpty ? ", and each enemy's fire comes from its own distance" : firstDistance is null ? "" : ", and all fire comes from the fight distance")] : Array.Empty<string>()),
             .. (options.ClearKills is { } clear ? [string.Create(CultureInfo.InvariantCulture,
                 $"Time to clear {clear} targets is when kill number {clear} lands in each fixed-length fight, counted from the first target's arrival; fights that don't reach it count as not cleared")] : Array.Empty<string>()),
             .. (firstDistance is null ? Array.Empty<string>() : [WeaponFalloff.Declared(resolved)
                 ? (distances.IsEmpty ? "Weapon damage falls off at the fight distance:" : "Weapon damage falls off at each target's distance:") + " full to optimal range, linear to long range, then the weapon's floor out to maximum range and none beyond it; a floor of 1 (also used for weapons without source data) has no falloff; headshot and critical damage fall off alike, statuses and talent damage don't; random damage variation is excluded"
                 : "The fight distance doesn't change weapon damage: the pack declares no falloff floor"]),
             options.IncludeIncoming ? "Shock, trigger-driven armor regeneration, Makeshift queues, pack-rule repairs and immediate critical-damage/kill/depletion repairs are modeled; ally repairs and other control effects are excluded" :
             "Status application and conditions cover one target; control effects and player repairs are excluded",
             pickupsUsed ? string.Create(CultureInfo.InvariantCulture, $"Assumed: each kill's dropped pickup is collected {options.PickupDelay:0.##} s later, {options.PickupChance:0.##%} of the time") :
                 "No selected talent collects dropped pickups"])
        {
            Coverage = WithSimulatedRecovery(EffectCoverage.Describe(pack.Pack, build, context), RecoveryKeys()) with
            {
                InputBasis = (options.DriveSimpleBuffs ? "Initial setup inputs; earned trigger values are reported in uptime statistics, not this snapshot." : "Fixed simulation assumptions; not measured proc uptime.") +
                (incomingInputs.Count == 0 ? "" : " Set by the pack for fights with enemy fire, not chosen: " + string.Join(", ", incomingInputs) + ".") +
                (distanceInputs.Count == 0 ? "" : " Set from " + (distances.IsEmpty ? "the fight distance" : "each target's distance") + ", not chosen: " + string.Join(", ", distanceInputs) + ".") +
                (coverSelector ? " Set by the cover cycle, not chosen: " + (pack.Pack.Selectors.First(selector => selector.ID == DummyFightOptions.CoverInput).Name) + "." : "")
            },
            MagazinePhaseUptime = PhaseMeans(magazineSeconds, fights, options.Duration),
            CyclePhaseUptime = PhaseMeans(cycleSeconds, fights, options.Duration),
            TimedTriggerUptime = triggerSeconds.ToImmutableDictionary(pair => pair.Key,
                pair => new TimedTriggerUptime(pair.Value.Seconds / fights, pair.Value.Seconds / fights / options.Duration,
                    pair.Value.StackSeconds / fights / options.Duration), StringComparer.Ordinal),
            TargetStatusUptime = statusSeconds.ToImmutableDictionary(pair => pair.Key,
                pair => new StatusUptime(pair.Value / fights, pair.Value / fights / options.Duration), StringComparer.Ordinal),
            SkillDps = skillStatistics?.Summary(),
            SkillUses = skillUseTotals.ToImmutableDictionary(pair => pair.Key, pair => new SkillSlotUse(pair.Value.Uses / fights, pair.Value.Held / fights), StringComparer.Ordinal),
            StatusDps = statusDps is null ? null : Summarize(statusDps),
            TalentDps = talentDps is null ? null : Summarize(talentDps),
            TotalDps = Summarize(totalDps),
            ZoneHits = zones is not null ? zoneHits / fights : null,
            Swaps = swaps / fights,
            Kills = kills / fights,
            KillTimes = killTimes.ToImmutable(),
            TimeToClear = clearTimes.Count == 0 ? null : Summarize([.. clearTimes]),
            ClearedFraction = options.ClearKills is null ? null : (double)clearTimes.Count / fights,
            TimeToKill = killTimeMeans.Count == 0 ? null : Summarize([.. killTimeMeans]),
            KilledFraction = targeted ? (double)killTimeMeans.Count / fights : null,
            Incoming = incoming is null ? null : new(enemyAttempts / fights, enemyHits / fights, Mean(incomingDamage), armorBreaks / fights, downed / fights,
                breakTimes.Count == 0 ? null : Summarize(breakTimes.ToArray()), downTimes.Count == 0 ? null : Summarize(downTimes.ToArray()))
            {
                BlockedShots = blockedShots / fights,
                RemovedHits = pack.Pack.Stats.Any(stat => stat.ID == EnemyFireSchedule.RemovedShareStat) ? removedHits / fights : null,
                Revives = skillFights?.Plans.Any(plan => plan.Revive is not null) == true ? revives / fights : null,
                DrawnHits = pack.Pack.Simulation.Skills.Any(rule => rule.DecoyHealth.Length > 0) ? drawnHits / fights : null,
                ArmorRepaired = Mean(armorRepairs),
                HealthRepaired = Mean(healthRepairs),
                ArmorKitsUsed = kitsUsed / fights,
                ArmorKitsCancelled = kitsCancelled / fights,
                ArmorKitAbilityUses = kitAbility is null ? null : abilityUses / fights,
                ArmorKitRepaired = kitRepaired / fights,
                ShieldAbsorbed = Mean(shieldAbsorbed),
                ShieldBreaks = shieldBreaks / fights,
                ShieldDeployments = shieldDeployments / fights,
                ShieldRepaired = Mean(shieldRepairs),
                ShieldDrained = drainUsed ? Mean(shieldDrained) : null,
                ShieldSwitches = shieldSwitches / fights,
                StatusDamageTaken = reportsPlayerStatuses ? Mean(statusDamageTaken) : null,
                PlayerStatusUptime = !reportsPlayerStatuses ? null : playerStatusSeconds.ToImmutableSortedDictionary(pair => pair.Key,
                    pair => new StatusUptime(pair.Value / fights, pair.Value / fights / options.Duration), StringComparer.Ordinal)
            }
        };
    }

    private static EffectCoverageReport WithSimulatedRecovery(EffectCoverageReport report, ImmutableHashSet<string> keys) => keys.IsEmpty ? report : report with
    {
        Talents = [.. report.Talents.Select(talent =>
        {
            var driven = talent.Channels.Where(channel => channel.Kind == EffectCoverage.AssumptionConditions)
                .SelectMany(channel => channel.Sources).Where(keys.Contains).ToImmutableArray();
            return driven.IsEmpty ? talent : talent with
            {
                Channels = talent.Channels.Add(new("simulated-recovery", driven,
                    "In this incoming-fire simulation the recovery model sets these from actual hits, kills and armor; the chosen assumption is not used for them."))
            };
        })]
    };

    private static void AccumulatePhases(Dictionary<string, Dictionary<string, double>> totals,
        ImmutableDictionary<string, ImmutableDictionary<string, double>> measurements)
    {
        foreach (var (key, values) in measurements)
        {
            if (!totals.TryGetValue(key, out var accumulated)) { totals[key] = accumulated = new(StringComparer.Ordinal); }
            foreach (var (value, seconds) in values) { accumulated[value] = accumulated.GetValueOrDefault(value) + seconds; }
        }
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>> PhaseMeans(
        Dictionary<string, Dictionary<string, double>> totals, int fights, double duration) =>
        totals.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value.ToImmutableDictionary(value => value.Key,
                value => new PhaseUptime(value.Value / fights, value.Value / fights / duration), StringComparer.Ordinal), StringComparer.Ordinal);

    private const string TargetProfileKey = DummyFightOptions.TargetProfileInput;

    // Only explicitly implemented recovery is allowed; later pack additions still fail closed.
    private static readonly Regex RecoveryStat = new("repair|regen|healing|on_kill|restor|recover", RegexOptions.CultureInvariant);

    /// <summary>The name of a rule's talent, specialization node or, for a skill's area (ADR 0016), skill.</summary>
    private static string RuleOwnerName(GamePack pack, string id) => pack.Talents.FirstOrDefault(talent => talent.ID == id)?.Name ??
        pack.ActiveSkills.FirstOrDefault(skill => skill.ID == id)?.Name ?? pack.Specializations.SelectMany(specialization => specialization.Nodes).First(node => node.ID == id).Name;

    /// <summary>Recovery-named stats that are not in-fight player recovery: skill repair scaling (skills are excluded
    /// from incoming fights), healing given to others, durations rather than amounts, and previews of repairs that pack
    /// rules make in the fight (Clutch, Preservation and Doctor Home's kit, R43).</summary>
    internal static readonly ImmutableHashSet<string> NotPlayerRecovery = ImmutableHashSet.Create(StringComparer.Ordinal,
        "repair_skills", "effective_repair_skills", "total_skill_repair", "outgoing_healing", "nanite_healing_efficiency", "makeshift_repairs_duration",
        "missing_armor_repair_on_crit_percent", "estimated_armor_repair_on_crit", "armor_repair_on_kill_over_time_percent",
        "estimated_armor_repair_on_kill_over_time", "doctor_home_kit_repair_percent");

    private sealed record RecoveryProfile(double KillArmor, double KillHealth, double BreakArmor, double ArmorPerSecond, double CriticalDamageShare, double MakeshiftShare, double MakeshiftDuration, double WeaponKillArmor)
    {
        /// <summary>The share of a shot's damage that repairs a raised shield (ADR 0026: Symbiosis).</summary>
        public double ShieldDamageShare { get; init; }
        /// <summary>The share of a raised shield's maximum health lost each whole second (ADR 0026: Symbiosis).</summary>
        public double ShieldDrainShare { get; init; }
    }

    /// <summary>A talent's shield drain: a share of the shield's maximum health, lost once a second (ADR 0026, R140).</summary>
    internal const string ShieldDrainStat = "shield_health_loss_share";

    private static readonly IReadOnlySet<string> NoTalents = new HashSet<string>(StringComparer.Ordinal);

    // ADR 0030: a repair skill use's repairs left and when it may next repair.
    private sealed class RepairUse
    {
        public double Remaining { get; set; }
        public double NextReady { get; set; } = double.NegativeInfinity;
    }

    /// <summary>Set settings no fight uses (SIMULATOR_COVERAGE_SURVEY.md): budgets, effects fights leave out, and repairs that step aside or are refused.</summary>
    private static readonly ImmutableHashSet<string> UnusedSetSettings = ImmutableHashSet.Create(StringComparer.Ordinal,
        "charging_retained_seconds", "umbra_shadow_stacks_to_consume", "ground_control_eligible_ally_repair", "return_to_sender_skill_damage", "true_patriot_blue_target",
        "true_patriot_white_ready", "aggressive_recon_active", "makeshift_qualifying_damage");

    /// <summary>
    /// The settings an active set's bonuses read that a driven fight leaves as set, by set, each with its value. One that moves nothing the
    /// set gives this build with any weapon the fight uses, whatever the set's other settings, isn't named.
    /// </summary>
    private static ImmutableArray<string> UndrivenSetSettings(CompiledPackExpressions pack, BuildSnapshot build, ResolvedBuild resolved, ImmutableDictionary<string, string> context,
        ImmutableArray<string> drivenKeys, string? secondSlot)
    {
        var named = ImmutableArray.CreateBuilder<string>();
        var numbers = ResolveAssumptions.Resolve(pack.Pack, context).Numeric;
        ImmutableArray<ImmutableDictionary<string, string>> weapons = secondSlot is null ? [context] : [context, context.SetItem(GamePack.WeaponSlotSelector, secondSlot)];
        foreach (var active in resolved.ActiveSets)
        {
            var set = pack.Pack.Sets.Single(entry => entry.ID == active.SetId);
            var modifiers = set.Bonuses.Where(bonus => active.AppliedBonuses.Contains(bonus.Pieces)).SelectMany(bonus => bonus.Modifiers).ToArray();
            var keys = modifiers.SelectMany(EffectCoverage.ContextReads).Distinct(StringComparer.Ordinal).Where(key => key != GamePack.FightContextSelector && key != GamePack.WeaponSlotSelector).ToArray();
            string On(SelectorDefinition selector) => modifiers.Select(modifier => modifier.RequiresContext?.GetValueOrDefault(selector.ID)).FirstOrDefault(value => value is not null) ??
                selector.Values.FirstOrDefault() ?? "";
            // A key's two ends: past a build's cap the current value may move nothing.
            (string Low, string High)? Ends(string key) => pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == key) is { } number
                ? (number.Min.ToString("R", CultureInfo.InvariantCulture), number.Max.ToString("R", CultureInfo.InvariantCulture))
                : pack.Pack.Selectors.FirstOrDefault(entry => entry.ID == key) is { } selector ? ("", On(selector)) : null;
            static ImmutableDictionary<string, string> With(ImmutableDictionary<string, string> at, string key, string value) => value.Length == 0 ? at.Remove(key) : at.SetItem(key, value);
            // What the set itself gives, so another talent reading its key doesn't count.
            string[] Given(ImmutableDictionary<string, string> at) => [.. BuildResolver.Resolve(pack, build, at).Calculation.Breakdowns.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.Contributions.Where(entry => entry.Source.Kind == "set" && entry.Source.Id == set.ID).Select(entry => $"{pair.Key} {entry.Op} {entry.Value:R}"))];
            var settings = new List<string>();
            foreach (var key in keys.Where(key => !drivenKeys.Contains(key) && !UnusedSetSettings.Contains(key)))
            {
                if (Ends(key) is not { } ends) { continue; }
                // As the user has the set's other settings, and with them all on, since some act only together (On Point's).
                var moves = weapons.SelectMany(weapon => new[] { weapon, keys.Aggregate(weapon, (next, other) => Ends(other) is { } open ? With(next, other, open.High) : next) })
                    .Any(at => !Given(With(at, key, ends.Low)).SequenceEqual(Given(With(at, key, ends.High))));
                if (!moves) { continue; }
                if (pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == key) is { } numeric)
                { settings.Add($"{numeric.Name}: {numeric.Shown(numbers.GetValueOrDefault(key, numeric.Default))}"); }
                else
                {
                    var selector = pack.Pack.Selectors.First(entry => entry.ID == key);
                    var now = context.GetValueOrDefault(key, selector.Default);
                    settings.Add($"{selector.Name}: {(now is "" or "false" ? "off" : now == "true" ? "on" : now)}");
                }
            }
            if (settings.Count > 0) { named.Add($"{set.Name} ({string.Join(", ", settings.Order(StringComparer.Ordinal))})"); }
        }
        return named.ToImmutable();
    }

    private static string SkillName(GamePack pack, string skill) => pack.ActiveSkills.FirstOrDefault(entry => entry.ID == skill)?.Name ?? skill;

    // The fight's one enemy distance, for a skill whose effects reach farther enemies later (ADR 0029); none with per-target distances.
    private static double? FightDistance(DummyFightOptions options) => options.TargetDistances.IsDefaultOrEmpty ? options.Distance : null;

    // The notes for skills that step aside: those the pack lists as playing no part in fights (ADR 0022), and those whose gate the
    // settings don't hold (ADR 0027), each with the use conditions dropped from their slots.
    private static IEnumerable<string> AsideNotes(GamePack pack, ImmutableDictionary<string, SimSkillAside> aside, string[] dropped)
    {
        foreach (var listed in new[] { true, false })
        {
            var slots = aside.Where(pair => pack.Simulation.SkillsAside.Any(entry => entry.Skill == pair.Value.Skill) == listed && !pair.Value.Why.StartsWith(SkillDamageSchedule.ArmorWaitNote, StringComparison.Ordinal) &&
                    pair.Value.Why != SkillDamageSchedule.ChargeNote && pair.Value.Why != SkillDamageSchedule.ChargeSkillsNote && pair.Value.Why != SkillDamageSchedule.HoldNote)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (slots.Length == 0) { continue; }
            var skills = string.Join("; ", slots.Select(pair => $"{pack.ActiveSkills.FirstOrDefault(skill => skill.ID == pair.Value.Skill)?.Name ?? pair.Value.Skill} ({pair.Value.Why})"));
            var uses = dropped.Where(slot => slots.Any(pair => pair.Key == slot)).ToArray();
            yield return (listed ? "Run without skills that play no part in fights: " : "Run without skills whose setting is off: ") + skills +
                (uses.Length == 0 ? "" : $". Use conditions dropped for: {string.Join(", ", uses)}");
        }
        // ADR 0030: skills used only once the player's armor falls, in a fight without enemy fire.
        var waiting = aside.Where(pair => pair.Value.Why.StartsWith(SkillDamageSchedule.ArmorWaitNote, StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (waiting.Length > 0)
        {
            var names = string.Join("; ", waiting.Select(pair => $"{pack.ActiveSkills.FirstOrDefault(skill => skill.ID == pair.Value.Skill)?.Name ?? pair.Value.Skill} ({pair.Value.Why})"));
            var uses = dropped.Where(slot => waiting.Any(pair => pair.Key == slot)).ToArray();
            yield return $"Run without skills used only once armor falls, since enemy fire is off: {names}" +
                (uses.Length == 0 ? "" : $". Use conditions dropped for: {string.Join(", ", uses)}");
        }
        // ADR 0031: buff hives, whose charges turn on buffs, in a fight without talent triggers.
        var charging = aside.Where(pair => pair.Value.Why == SkillDamageSchedule.ChargeNote).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (charging.Length > 0)
        {
            var names = string.Join(", ", charging.Select(pair => SkillName(pack, pair.Value.Skill)));
            var uses = dropped.Where(slot => charging.Any(pair => pair.Key == slot)).ToArray();
            yield return $"Run without skills whose charges turn on buffs, since talent triggers are off: {names}" +
                (uses.Length == 0 ? "" : $". Use conditions dropped for: {string.Join(", ", uses)}");
        }
        // ADR 0032: skills whose uses hold a buff, in a fight without talent triggers.
        var holding = aside.Where(pair => pair.Value.Why == SkillDamageSchedule.HoldNote).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (holding.Length > 0)
        {
            var names = string.Join(", ", holding.Select(pair => SkillName(pack, pair.Value.Skill)));
            var uses = dropped.Where(slot => holding.Any(pair => pair.Key == slot)).ToArray();
            yield return $"Run without skills that turn on a buff while out, since talent triggers are off: {names}" +
                (uses.Length == 0 ? "" : $". Use conditions dropped for: {string.Join(", ", uses)}");
        }
        // ADR 0031: hives charging skills, with no other skill in the fight whose uses they could reach.
        var unreached = aside.Where(pair => pair.Value.Why == SkillDamageSchedule.ChargeSkillsNote).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (unreached.Length > 0)
        {
            var names = string.Join(", ", unreached.Select(pair => SkillName(pack, pair.Value.Skill)));
            var uses = dropped.Where(slot => unreached.Any(pair => pair.Key == slot)).ToArray();
            yield return $"Run without hives charging skills, since no other skill in the fight runs a use for them to reach: {names}" +
                (uses.Length == 0 ? "" : $". Use conditions dropped for: {string.Join(", ", uses)}");
        }
    }
    /// <summary>A talent's shield repair: a share of the damage each shot deals (ADR 0026, R140).</summary>
    internal const string ShieldDamageShareStat = "shield_repair_damage_share";
    private static readonly ImmutableHashSet<string> SupportedRecovery = ImmutableHashSet.Create(StringComparer.Ordinal,
        "armor_on_kill", "armor_on_kill_percent", "estimated_armor_on_kill", "armor_repair_per_kill_percent", "estimated_armor_repair_per_kill",
        "estimated_kill_repair_per_second", "health_on_kill", "estimated_health_on_kill", "armor_repair_on_depletion_percent", "estimated_armor_repair_on_depletion",
        "armor_regeneration", "armor_regeneration_percent", "estimated_armor_regeneration", "weapon_crit_repair_share", "estimated_crit_repair_per_second",
        "makeshift_repairs_share", "makeshift_repair_budget", "makeshift_repair_average_rate", ShieldDamageShareStat, "estimated_shield_repair_per_second");

    /// <summary>
    /// A set talent's repair whose trigger no fight reaches (ADR 0021): its stats don't refuse a fight under enemy fire, which runs
    /// without the repair. One that replaces the armor kit (Hackstep Protocol) steps aside only in a fight without kits.
    /// </summary>
    private sealed record UnreachedRepair(string Talent, ImmutableHashSet<string> Stats, string Why);

    private static readonly ImmutableArray<UnreachedRepair> UnreachedRepairSets =
    [
        new("gear_talent_ground_control", ImmutableHashSet.Create(StringComparer.Ordinal, "ground_control_proximity_repair_percent", "ground_control_proximity_repair_budget"),
            "it repairs only when you repair an ally, and fights have no allies"),
        new("gear_talent_from_the_shadows_into_the_light", ImmutableHashSet.Create(StringComparer.Ordinal, "umbra_shadow_repair_per_stack", "umbra_shadow_available_repair_per_stack",
            "umbra_shadow_full_stack_repair_capacity", "umbra_shadow_repair_budget", "umbra_shadow_average_repair_rate"),
            "Shadow stacks built in the open repair only as they're spent in cover, and fights never move between the two"),
        new("gear_talent_hackstep_protocol", ImmutableHashSet.Create(StringComparer.Ordinal, "hackstep_armor_repair_share", "hackstep_available_armor_repair"),
            "it is the build's armor kit, and this fight uses no kits"),
    ];

    // A stat and every stat whose formula reads it, directly or through another: whole identifiers only.
    private static HashSet<string> StatsReading(GamePack pack, string id)
    {
        var found = new HashSet<string>(StringComparer.Ordinal) { id };
        var formulas = pack.Stats.ToDictionary(stat => stat.ID, stat => Regex.Matches(stat.Formula, "[A-Za-z_][A-Za-z0-9_]*").Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (stat, reads) in formulas)
            {
                if (!found.Contains(stat) && reads.Overlaps(found)) { found.Add(stat); grew = true; }
            }
        }
        return found;
    }

    private static RecoveryProfile ReadIncomingRecovery(ResolvedBuild resolved, IReadOnlySet<string> unreached)
    {
        var recovery = resolved.Calculation.Stats.Where(pair => pair.Value != 0 && RecoveryStat.IsMatch(pair.Key) && !NotPlayerRecovery.Contains(pair.Key) && !SupportedRecovery.Contains(pair.Key) &&
                !unreached.Contains(pair.Key))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).FirstOrDefault();
        if (recovery is not null) { throw new SimulationException("incoming-repair-unsupported", $"Incoming fire does not yet support {recovery}"); }
        foreach (var id in new[] { "estimated_kill_repair_per_second", "estimated_crit_repair_per_second", "estimated_shield_repair_per_second" })
        {
            if (resolved.Calculation.Breakdowns.TryGetValue(id, out var summary) && !summary.Contributions.IsEmpty)
            { throw new SimulationException("incoming-repair-unsupported", "Modifiers on repair-per-second summaries have no discrete repair rule"); }
        }
        foreach (var (source, output) in new[] {
            ("armor_on_kill", "estimated_armor_on_kill"), ("armor_on_kill_percent", "estimated_armor_on_kill"),
            ("armor_repair_per_kill_percent", "estimated_armor_repair_per_kill"), ("estimated_kill_repair_per_second", "estimated_armor_repair_per_kill"),
            ("armor_regeneration", "estimated_armor_regeneration"), ("armor_regeneration_percent", "estimated_armor_regeneration"),
            ("health_on_kill", "estimated_health_on_kill"), ("armor_repair_on_depletion_percent", "estimated_armor_repair_on_depletion") })
        {
            if (resolved.Calculation.Stats.GetValueOrDefault(source) != 0 && !resolved.Calculation.Stats.ContainsKey(output))
            { throw new SimulationException("incoming-repair-invalid", $"Active {source} requires the resolved {output} amount"); }
        }
        double Read(string id)
        {
            var value = resolved.Calculation.Stats.GetValueOrDefault(id);
            if (!double.IsFinite(value) || value < 0) { throw new SimulationException("incoming-repair-invalid", $"Invalid {id}"); }
            return value;
        }
        var genericKillArmor = Read("estimated_armor_on_kill");
        var weaponKillArmor = Read("estimated_armor_repair_per_kill");
        if (!double.IsFinite(genericKillArmor + weaponKillArmor)) { throw new SimulationException("incoming-repair-invalid", "Combined armor-on-kill repair overflowed"); }
        var regeneration = Read("estimated_armor_regeneration");
        var criticalShare = Read("weapon_crit_repair_share");
        if (Read("estimated_crit_repair_per_second") > 0 && criticalShare == 0)
        { throw new SimulationException("incoming-repair-invalid", "A critical repair summary requires weapon_crit_repair_share"); }
        var makeshiftShare = Read("makeshift_repairs_share");
        var makeshiftDuration = Read("makeshift_repairs_duration");
        if (makeshiftShare > 1 || makeshiftShare > 0 && (makeshiftDuration <= 0 || makeshiftDuration > 600))
        { throw new SimulationException("incoming-repair-invalid", "Makeshift Repairs needs a share in 0-1 and a positive duration up to 600 seconds"); }
        if (Read("makeshift_repair_budget") != 0 || Read("makeshift_repair_average_rate") != 0)
        { throw new SimulationException("incoming-repair-unsupported", "Makeshift previews must be zero; actual received damage creates queued budgets"); }
        // Symbiosis drains the shield it repairs (R140), so a repair share runs only with the drain beside it.
        var shieldShare = Read(ShieldDamageShareStat);
        var shieldDrain = Read(ShieldDrainStat);
        if (shieldShare > 0 && shieldDrain == 0)
        { throw new SimulationException("incoming-repair-unsupported", $"A shield repair from damage needs its drain, {ShieldDrainStat}, beside it (ADR 0026)"); }
        if (shieldDrain > 1) { throw new SimulationException("incoming-repair-invalid", $"{ShieldDrainStat} must be a share from 0 to 1"); }
        return new(genericKillArmor + weaponKillArmor, Read("estimated_health_on_kill"), Read("estimated_armor_repair_on_depletion"),
            regeneration, criticalShare, makeshiftShare, makeshiftDuration, weaponKillArmor)
        { ShieldDamageShare = shieldShare, ShieldDrainShare = shieldDrain };
    }

    /// <summary>A player down with a revive on its way (ADR 0037): the pool they went down with bleeds at a steady rate and takes damage.</summary>
    private sealed class DownedPlayer(double since, double reviveAt, SimSkillRevive rule, double armor, double pool)
    {
        public double Since { get; } = since;
        public double ReviveAt { get; } = reviveAt;
        public SimSkillRevive Rule { get; } = rule;
        public double Armor { get; } = armor;
        public double Taken { get; set; }
        private double BleedPerSecond => pool / Rule.BleedSeconds;
        public double Left(double at) => pool - Taken - (at - Since) * BleedPerSecond;
        /// <summary>When the pool runs out if nothing more lands; spent, at once.</summary>
        public double DeathAt => Since + (pool - Taken) / BleedPerSecond;
        public double Due => Math.Min(ReviveAt, DeathAt);
    }

    /// <summary>A use's zones on an enemy (ADR 0036): the use, its multiplier's factor, and the zones' health left.</summary>
    private sealed class ZoneMark(FightEnemy enemy, double use, double factor)
    {
        public FightEnemy Enemy { get; } = enemy;
        public double Use { get; } = use;
        public double Factor { get; } = factor;
        public double Left { get; set; }
    }

    private sealed record ShotProfile(double Magazine, double Interval, double Reload, bool NoReload, double Hit, double Head, double Crit, double BodyDamage, double HeadDamage, double CritDamage, double CritHeadDamage, double Expected, double Pellets)
    {
        public double ArmorFactor { get; init; } = 1;
        public double HealthFactor { get; init; } = 1;
        public double StatusDurationFactor { get; init; } = 1;
        /// <summary>The share of body hits on an enemy with marked zones landing on one (ADR 0036), and a zone hit's multiplier.</summary>
        public double ZoneShare { get; init; }
        public double ZoneMultiplier { get; init; } = 1;
        /// <summary>A zone hit's whole-shot damage: the body hit's, with the zone's multiplier added to critical damage as a headshot's is.</summary>
        public double ZoneDamage(bool critical, double factor) => (critical ? CritDamage : BodyDamage) + BodyDamage * (Math.Max(1, ZoneMultiplier * factor) - 1);
        public double SwapSpeed { get; init; }
        public double SwitchMultiplier { get; init; } = 1;
        /// <param name="distance">The fight distance, for weapon falloff when the pack declares a floor.</param>
        /// <param name="zones">The zones a skill in the fight marks (ADR 0036), whose share and multiplier the profile reads.</param>
        public static ShotProfile Read(ResolvedBuild resolved, bool targeted, double? distance, SimSkillZones? zones)
        {
            var stats = resolved.Calculation.Stats;
            double Read(string id) => WeaponTiming.Require(stats, id);
            // These require per-shot or per-magazine mechanics, which this first slice does not model.
            foreach (var id in new[] { "augment_echo", "practical_shot_damage_bonus", "practical_shot_bonus_damage", "practical_magazine_bonus_damage", "practical_rounds_returned_per_shot" })
            {
                if (stats.GetValueOrDefault(id) != 0)
                { throw new SimulationException("shot-mechanic-unsupported", $"Dummy foundation does not yet support {id}"); }
            }
            var (magazine, interval, reload, noReload) = WeaponTiming.Read(stats);
            var pellets = Read("equipped_weapon_pellets");
            if (pellets < 1 || pellets > 1000 || pellets != Math.Floor(pellets))
            { throw new SimulationException("weapon-stat-invalid", "1-1000 whole pellets are required"); }
            var hit = Math.Clamp(Read("practical_hit_fraction"), 0, 1);
            var head = Math.Clamp(Read("practical_headshot_fraction"), 0, 1);
            var crit = Math.Clamp(Read("effective_critical_hit_chance"), 0, 1);
            // Falloff scales every hit's damage alike; statuses and talent damage a hit starts keep their own.
            var falloff = WeaponFalloff.Factor(resolved, distance);
            var bodyDamage = Read("equipped_weapon_damage") * falloff;
            var headDamage = Read("equipped_weapon_headshot_damage") * falloff;
            var critDamage = Read("equipped_weapon_critical_damage") * falloff;
            var critHeadDamage = Read("equipped_weapon_critical_headshot_damage") * falloff;
            var expected = Read("equipped_weapon_practical_shot_damage") * falloff;
            var armorFactor = targeted ? Read("out_of_cover_damage_factor") * (1 + Read("damage_to_armor")) : 1;
            var healthFactor = targeted ? Read("out_of_cover_damage_factor") * (1 + Read("health_damage")) : 1;
            var statusFactor = 1 + stats.GetValueOrDefault("status_effects");
            if (!double.IsFinite(statusFactor) || statusFactor <= 0) { throw new SimulationException("weapon-stat-invalid", "Invalid status duration factor"); }
            if (!double.IsFinite(armorFactor) || !double.IsFinite(healthFactor)) { throw new SimulationException("weapon-stat-invalid", "Target factors overflowed"); }
            return new(magazine, interval, reload, noReload, hit, head, crit, bodyDamage, headDamage, critDamage, critHeadDamage, expected, pellets)
            {
                ArmorFactor = armorFactor,
                HealthFactor = healthFactor,
                StatusDurationFactor = statusFactor,
                ZoneShare = zones is null ? 0 : Math.Clamp(Read(zones.Share), 0, 1),
                ZoneMultiplier = zones is null ? 1 : Read(zones.Multiplier),
                SwapSpeed = stats.GetValueOrDefault("effective_swap_speed", 0),
                SwitchMultiplier = stats.GetValueOrDefault("weapon_switch_speed_multiplier", 1)
            };
        }
    }

    // Nonnegative samples keep each incremental difference and mean finite, where a plain sum could overflow.
    private static double Mean(double[] values)
    {
        var mean = 0d;
        for (var i = 0; i < values.Length; i++) { mean += (values[i] - mean) / (i + 1); }
        return mean;
    }

    private sealed class SkillStatistics(int fights)
    {
        private readonly double[] total = new double[fights], impact = new double[fights], continuous = new double[fights];

        public void Record(int fight, double totalDamage, double impactDamage, double continuousDamage, double duration)
        {
            var totalRate = totalDamage / duration;
            var impactRate = impactDamage / duration;
            var continuousRate = continuousDamage / duration;
            if (!double.IsFinite(totalRate) || !double.IsFinite(impactRate) || !double.IsFinite(continuousRate))
            { throw new SimulationException("damage-overflow", "Skill DPS overflowed"); }
            total[fight] = totalRate;
            impact[fight] = impactRate;
            continuous[fight] = continuousRate;
        }

        public SkillDpsSummary Summary() => new(Summarize(total), Summarize(impact), Summarize(continuous));
    }

    private static FightSummary Summarize(double[] values)
    {
        Array.Sort(values);
        var mean = Mean(values);
        var scale = values.Max(value => Math.Abs(value - mean));
        var deviation = scale == 0 ? 0 : scale * Math.Sqrt(values.Sum(value => Math.Pow((value - mean) / scale, 2) / values.Length));
        double Percentile(double fraction) => values[(int)Math.Round(fraction * (values.Length - 1), MidpointRounding.AwayFromZero)];
        return new(mean, deviation, values[0], Percentile(0.05), Percentile(0.5), Percentile(0.95), values[^1]);
    }
}
