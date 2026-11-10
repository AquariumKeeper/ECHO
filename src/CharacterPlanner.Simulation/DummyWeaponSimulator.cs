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
        options ??= new();
        options.Validate();
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
        PhaseTriggerState? phases = null;
        CombatTriggerState? combat = null;
        EnemyCountState? enemyCounts = null;
        var uncountedKeys = ImmutableArray<string>.Empty;
        var kitlessTalents = ImmutableArray<string>.Empty;
        var skilllessTalents = ImmutableArray<string>.Empty;
        var statusRules = ImmutableArray<SimTriggerDefinition>.Empty;
        var dotRules = ImmutableArray<SimStatusDamageDefinition>.Empty;
        var talentDamageRules = ImmutableArray<SimTalentDamageDefinition>.Empty;
        var drivenKeys = ImmutableArray<string>.Empty;
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
            // Only a fight with several enemies knows who's around. One at a time, the talents read the user's assumption.
            var countRules = options.EnemiesAtOnce is null ? [] : groups[nameof(EnemyCountState)].ToArray();
            // A count on a status replaces the reading of the enemy shot (Bleeding Edge), which still runs with one enemy at a time.
            statusRules = [.. groups[nameof(StatusTriggerState)].Where(rule => !countRules.Any(count => count.Key == rule.Key))];
            uncountedKeys = options.EnemiesAtOnce is null ? [.. groups[nameof(EnemyCountState)].Select(rule => rule.Key)
                .Where(key => !statusRules.Any(rule => rule.Key == key)).Distinct().Order(StringComparer.Ordinal)] : [];
            // A rule that requires selector values the fight's context doesn't hold (Calculated outside cover) takes no part.
            var fightContext = context;
            buffRules = [.. groups[nameof(TimedBuffState)].Where(rule => rule.RequiresContext is not { } gate ||
                gate.All(pair => fightContext.GetValueOrDefault(pair.Key, "") == pair.Value))];
            // With no kits, a rule that gains on one steps aside and its talent reads the user's assumption, as counts do one
            // enemy at a time. A talent on gear is no reason to refuse the fight, and the result names it.
            if (options.ArmorKits is null)
            {
                static bool OnKit(SimTriggerDefinition rule) => rule.On == "armor_kit" || rule.Also.Any(gain => gain.On == "armor_kit");
                kitlessTalents = [.. pack.Pack.Simulation.Triggers.Where(rule => selected.Contains(rule.Talent) && OnKit(rule)).Select(rule => rule.Talent)
                    .Concat(pack.Pack.Simulation.TalentDamage.Where(rule => selected.Contains(rule.Talent) && rule.On == "armor_kit").Select(rule => rule.Talent))
                    .Distinct(StringComparer.Ordinal).Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
                buffRules = [.. buffRules.Where(rule => rule.On != "armor_kit" || !rule.Also.IsEmpty)];
                statusRules = [.. statusRules.Where(rule => rule.On != "armor_kit" || !rule.Also.IsEmpty)];
            }
            // With no skills, a skill's hit or kill never comes. Its rules stay, so their keys stay driven and off unless another event gains
            // them (Outsider's and Preservation's weapon kills), and never take the user's setting; the result names their talents.
            if (!options.IncludeSkills)
            {
                static bool OnSkill(string name) => name is "skill_hit" or "skill_kill";
                skilllessTalents = [.. buffRules.Concat(statusRules).Concat(countRules)
                    .Where(rule => OnSkill(rule.On) || rule.Also.Any(gain => OnSkill(gain.On)) || OnSkill(rule.ResetOn) || OnSkill(rule.AlsoResetOn) || OnSkill(rule.ConsumeOn) || OnSkill(rule.CancelOn))
                    .Select(rule => rule.Talent)
                    .Concat(pack.Pack.Simulation.TalentDamage.Where(rule => selected.Contains(rule.Talent) && OnSkill(rule.On)).Select(rule => rule.Talent))
                    .Distinct(StringComparer.Ordinal).Select(id => RuleOwnerName(pack.Pack, id)).Order(StringComparer.Ordinal)];
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
            combat = new CombatTriggerState(pack, combatRules);
            if (combat.NeedsDistance && firstDistance is null) { throw new SimulationException("distance-required", "Selected distance rules require an explicit fight distance"); }
            context = combat.Context(context, firstDistance ?? 0, 0, 100);
            if (countRules.Length > 0)
            {
                enemyCounts = new EnemyCountState(pack, countRules);
                if (enemyCounts.NeedsDistance && firstDistance is null) { throw new SimulationException("distance-required", "Selected enemy counts require an explicit fight distance"); }
                context = enemyCounts.Context(context, (rule, _) => CountPresent(Enumerable.Range(0, options.EnemiesAtOnce!.Value), rule.Within));
            }
            var supportedEvents = new[] { "shot", "hit", "hit_pellet", "crit", "crit_pellet", "headshot", "body_hit", "unarmored_hit", "kill", "crit_kill", "headshot_kill", "reload", "reload_end", "status_applied", "armor_kit", "pickup", "second" };
            if (options.FocusSeconds is not null) { supportedEvents = [.. supportedEvents, "target_changed"]; }
            // Without skills they're never emitted, so their rules simply never fire (named above).
            supportedEvents = [.. supportedEvents, "skill_hit", "skill_kill"];
            if (options.IncludeIncoming) { supportedEvents = [.. supportedEvents, "damaged", "armor_break"]; }
            if (options.IncludeShield) { supportedEvents = [.. supportedEvents, "shield_break"]; }
            if (options.Rotation is not null) { supportedEvents = [.. supportedEvents, "swap", "swapped", "swap_after_kill"]; }
            // A skill's kill is already a kill to gear talents, so a gear talent's count naming both would count it twice.
            if (statusRules.Any(rule => gearTalents.Contains(rule.Talent) && new[] { rule.On }.Concat(rule.Also.Select(gain => gain.On)).ToHashSet(StringComparer.Ordinal).IsSupersetOf(["kill", "skill_kill"])))
            { throw new SimulationException("trigger-unsupported", "A gear talent's kill already counts a skill's kill, so its count can't name skill_kill as well"); }
            if (statusRules.Concat(countRules).Any(rule => rule.On.Length > 0 && !supportedEvents.Contains(rule.On) || rule.Also.Any(gain => !supportedEvents.Contains(gain.On))))
            { throw new SimulationException("trigger-event-unsupported", "A status or count rule requires an unimplemented event"); }
            // A single weapon never swaps, so a swap event that only ends, resets or consumes a rule (Frenzy's removal on weapon
            // switch) simply never fires; one that gains still needs rotation.
            string[] neverFires = [.. options.Rotation is null ? ["swap", "swapped", "swap_after_kill"] : Array.Empty<string>(),
                .. options.FocusSeconds is null ? ["target_changed"] : Array.Empty<string>()];
            // A holstered-gain rule (Quick Draw) may gain on time alone, with no event. Only buff-type rules hear target_death.
            string[] buffEvents = [.. supportedEvents, "target_death"];
            bool Clears(string name) => name.Length == 0 || buffEvents.Contains(name) || neverFires.Contains(name);
            if (buffRules.Any(rule => rule.On.Length > 0 && !buffEvents.Contains(rule.On) ||
                rule.Also.Any(gain => !buffEvents.Contains(gain.On)) ||
                !Clears(rule.ResetOn) || !Clears(rule.AlsoResetOn) || !Clears(rule.ConsumeOn) || !Clears(rule.CancelOn)))
            { throw new SimulationException("trigger-event-unsupported", "A selected rule requires an unimplemented event"); }
            context = new StatusTriggerState(pack, statusRules).Context(context, 0);
            // Only statuses a selected talent applies tick; observer rules never apply one.
            dotRules = [.. pack.Pack.Simulation.StatusDamage.Where(damage => statusRules.Any(rule => rule.On.Length > 0 && rule.Status == damage.Status))];
            // A selected talent's own damage over time (Toxic Delivery) ticks beside the statuses.
            talentDamageRules = [.. pack.Pack.Simulation.TalentDamage.Where(damage => selected.Contains(damage.Talent))];
            if (talentDamageRules.Any(rule => !supportedEvents.Contains(rule.On)))
            { throw new SimulationException("trigger-event-unsupported", "A talent damage rule requires an unimplemented event"); }
            var setupBuffs = new TimedBuffState(buffRules);
            setupBuffs.ValidatePack(pack);
            context = setupBuffs.Context(context, 0);
            bool Uses(string name) => buffRules.Concat(statusRules).Concat(countRules).Any(rule => rule.On == name || rule.Also.Any(gain => gain.On == name) ||
                rule.ResetOn == name || rule.AlsoResetOn == name || rule.ConsumeOn == name || rule.CancelOn == name);
            pickupsUsed = Uses("pickup");
            secondsUsed = Uses("second");
            reloadEndUsed = Uses("reload_end");
            hitPelletUsed = Uses("hit_pellet") || talentDamageRules.Any(rule => rule.On == "hit_pellet");
            // Bank keys are driven too: a pool cap changes them, and resolved states are cached by every driven key.
            drivenKeys = [.. buffRules.Concat(statusRules).Concat(readings).Select(rule => rule.Key)
                .Concat(sequence.IsEmpty ? [] : [TargetProfileKey])
                .Concat(bandInputs)
                .Concat(buffRules.Where(rule => rule.Bank.Length > 0).Select(rule => rule.Bank))
                .Concat(options.Rotation is not null ? [GamePack.WeaponSlotSelector] : []).Distinct().Order(StringComparer.Ordinal)];
            fightBuffRules = buffRules;
            // A trigger talent on both rotated weapons runs one copy of its rules per weapon, each with its own keys,
            // pools and banks, and only the copy in hand drives the talent's keys (a holstered talent doesn't react).
            var copied = weaponTalents.Count == 2 ? weaponTalents.Values.First().Intersect(weaponTalents.Values.Last())
                .Where(talent => buffRules.Concat(statusRules).Any(rule => rule.Talent == talent)).ToHashSet(StringComparer.Ordinal) : [];
            if (copied.Count > 0)
            {
                if (statusRules.Any(rule => copied.Contains(rule.Talent)))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A status talent on both swapped weapons would apply to one shared target"); }
                if (pack.Pack.Talents.Any(talent => copied.Contains(talent.ID) && talent.Modifiers.Any(modifier => modifier.WhileHolstered)))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A talent on both swapped weapons with holstered effects would need both copies' state at once"); }
                var copiedRules = buffRules.Where(rule => copied.Contains(rule.Talent)).ToArray();
                var names = copiedRules.SelectMany(rule => new[] { rule.Key, rule.Bank }).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
                var pools = copiedRules.Select(rule => rule.Pool).Where(pool => pool.Length > 0).ToHashSet(StringComparer.Ordinal);
                if (buffRules.Concat(statusRules).Concat(readings).Any(rule => !copied.Contains(rule.Talent) &&
                    (names.Contains(rule.Key) || names.Contains(rule.Bank) || pools.Contains(rule.Pool) || names.Contains(rule.WhileKey))))
                { throw new SimulationException("rotation-shared-talent-unsupported", "A talent on both swapped weapons shares a key or pool with another talent"); }
                foreach (var name in names) { copiedKeys[name] = weaponTalents.Keys.ToDictionary(slot => slot, slot => name + "@" + slot, StringComparer.Ordinal); }
                string For(string name, string slot) => name.Length == 0 ? "" : name + "@" + slot;
                fightBuffRules = [.. buffRules.Where(rule => !copied.Contains(rule.Talent)), .. weaponTalents.Keys.SelectMany(slot => copiedRules.Select(rule => rule with
                {
                    Talent = For(rule.Talent, slot),
                    Key = For(rule.Key, slot),
                    Pool = For(rule.Pool, slot),
                    Bank = For(rule.Bank, slot)
                }))];
                // Each weapon's copy reacts as that weapon's talent.
                foreach (var (slot, owned) in weaponTalents) { owned.UnionWith(copied.Select(talent => For(talent, slot))); }
            }
        }
        var resolved = BuildResolver.Resolve(pack, build, context);
        if (resolved.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
        { throw new SimulationException("resolve-failed", string.Join("; ", resolved.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))); }
        var active = context.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
        if (!resolved.Loadout.Equipped.Any(item => item.WeaponSlot && item.SlotId == active))
        { throw new SimulationException("weapon-required", "Select an equipped active weapon"); }
        var stats = resolved.Calculation.Stats;
        var initialWeapon = ShotProfile.Read(resolved, targeted, firstDistance);
        var incoming = options.IncludeIncoming ? EnemyFireSchedule.Create(resolved) : null;
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
        // Clutch, Doctor Home and Preservation repair through their pack rules (R43); recovery here is the generic kind.
        RecoveryProfile ReadRecovery(ResolvedBuild current)
        {
            var profile = ReadIncomingRecovery(current);
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
            var fullSetTalents = SetBonusSelection.FullSetTalents(pack.Pack, resolved.ActiveSets).ToHashSet(StringComparer.Ordinal);
            if (armorKit.ReplacedBySpecializations.Contains(build.SpecializationId) || armorKit.ReplacedByTalents.Any(id => selected.Contains(id) || fullSetTalents.Contains(id)))
            { throw new SimulationException("armor-kits-unsupported", "This build replaces the armor kit, which fights don't model yet; run it without kits"); }
        }
        if (options.FocusSeconds is not null && DummyFightOptions.FocusProblem(options.EnemiesAtOnce) is { } focusProblem)
        { throw new SimulationException("focus-unsupported", focusProblem); }
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
            profiles[plan.SecondSlot] = ShotProfile.Read(template.Resolution(plan.SecondSlot), targeted, firstDistance);
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
        foreach (var (slot, owned) in weaponTalents.Where(pair => profiles.ContainsKey(pair.Key)))
        { foreach (var talent in owned) { magazines[talent] = profiles[slot].Magazine; } }
        // Gated status rules (Ardent's burn) read the driven keys at their event, and a bullet's events read them at its firing.
        var gated = statusRules.Any(rule => rule.WhileKey.Length > 0);
        var holdingTalents = profiles.Keys.ToDictionary(slot => slot, slot => template is null ? null :
            (IReadOnlySet<string>)new HashSet<string>(gearTalents.Concat(weaponTalents.GetValueOrDefault(slot) ?? Enumerable.Empty<string>()), StringComparer.Ordinal), StringComparer.Ordinal);
        var armor = targeted ? WeaponTiming.Require(stats, "enemy_target_armor") : 0;
        var health = targeted ? WeaponTiming.Require(stats, "enemy_target_health") : 1;
        if (health <= 0) { throw new SimulationException("target-invalid", "Targets need positive health"); }
        // Each profile in a target sequence resolves its own pools and its own enemy fire.
        var targetInput = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == TargetProfileKey);
        var sequencePools = new Dictionary<int, (double Armor, double Health)>();
        var sequenceFire = new Dictionary<int, EnemyFireSchedule>();
        // Each weapon's skill schedule against each profile, with the same timing as the chosen profile's (P10).
        var sequenceSkills = new Dictionary<string, Dictionary<int, SkillDamageSchedule>>(StringComparer.Ordinal);
        var skillSchedule = options.IncludeSkills ? SkillDamageSchedule.Create(pack.Pack, resolved.ActiveSkillPreviews, options.IncludeShield) : null;
        // Each weapon's skills as fights start. A second weapon is held to its own timing against mixed targets.
        var heldSkills = new Dictionary<string, SkillDamageSchedule>(StringComparer.Ordinal);
        if (skillSchedule is not null)
        {
            heldSkills[active] = skillSchedule;
            if (options.Rotation is { } skillRotation)
            { heldSkills[skillRotation.SecondSlot] = SkillDamageSchedule.Create(pack.Pack, template!.Resolution(skillRotation.SecondSlot).ActiveSkillPreviews, options.IncludeShield); }
        }
        // A set talent's rule can reach what a fight holds fixed: Apex Engine's stacks raise Skill Tier, and a shield doesn't follow. So can
        // Energize's kit, whose Overcharge lifts a shield even at tier 6 (R64), and any rule waiting on a stat. A fight would only be refused
        // when the first stack lands, a kit is used or the gate opens, so the rolls would decide. It's checked here, at the rule's height, so
        // they can't. Skills follow, so they need no check (ADR 0012).
        if (shieldProfile is not null || incoming is not null)
        {
            foreach (var rule in pack.Pack.Simulation.Triggers.Where(rule => setTalents.Contains(rule.Talent) ||
                options.ArmorKits is not null && options.DriveSimpleBuffs && rule.On == "armor_kit" && gearTalents.Contains(rule.Talent) ||
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
                    var profileSkills = SkillDamageSchedule.Create(pack.Pack, profiled.ActiveSkillPreviews, options.IncludeShield);
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
        var talentDps = talentDamageRules.IsEmpty ? null : new double[fights];
        var totalDps = new double[fights];
        long work = 0;
        double shots = 0, reloads = 0, swaps = 0;
        double enemyAttempts = 0, enemyHits = 0, armorBreaks = 0, downed = 0, blockedShots = 0, kitsUsed = 0, kitsCancelled = 0, kitRepaired = 0;
        var incomingDamage = new double[fights];
        var armorRepairs = new double[fights];
        var healthRepairs = new double[fights];
        var shieldRepairs = new double[fights];
        var shieldAbsorbed = new double[fights];
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
            var fightKills = 0;
            var fightKillSeconds = 0d;
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
            var shield = shieldProfile?.CreateState();
            // Set by a break while the shield bars a weapon; the build's weapon returns at the next decision point.
            var shieldReturn = false;
            var recoveryProfile = initialRecovery;
            // Independent armor repairs over time: armor_repair grants with a duration (Preservation, R41).
            var grantRepairs = new TimedArmorRepairQueue();
            var recoveryTime = 0d;
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
            var reloadCompleting = false;
            // With a cover cycle: the earliest the next shot may fire (the fire interval outlasts a short cover phase), and the
            // last cycle whose cover phase has had its reload decision.
            var shotReady = 0d;
            var coveredCycle = -1d;
            var kits = armorKit is null ? null : new ArmorKitState(armorKit, options.ArmorKits!.Value, options.ArmorKitAtPercent!.Value);
            var random = new SimulationRandom(options.Seed, (ulong)fight);
            double total = 0, time = 0, ammo = initialWeapon.Magazine;
            var magazine = options.DriveSimpleBuffs && template is null ? phases!.TrackMagazine(initialWeapon.Magazine) : null;
            var counts = enemyCounts?.Fresh();
            var heldMagazine = options.DriveSimpleBuffs && template is not null && !magazineRules.IsEmpty ? new HeldMagazinePhaseState(magazineRules) : null;
            void SetAmmo(double value, double at)
            {
                magazine?.Observe(value, Math.Min(at, options.Duration));
                ammo = value;
            }
            var rotation = template?.Fresh();
            double ArmorFactor(string stat) => stat.Length == 0 ? 1 : 1 + (heldTicks is null ? statusTicks : heldTicks[rotation!.ActiveSlot])[stat];
            // The weapon in hand changes when a swap starts; the rotation's active slot changes when the swap is scheduled.
            var hand = active;
            // Holstered-gain rules (Quick Draw) start gaining when the fight starts without one of their weapons in hand.
            buffs?.Hold(0, weaponTags[hand]);
            var lastWeaponKills = new Dictionary<string, double>(StringComparer.Ordinal);
            // Swap and reload starts of scheduled rotation actions, fired in time order with the fight's other events.
            // Swap starts, reload starts and, when a rule reads it, reload completions, in time order.
            var actionEvents = new Queue<(double At, WeaponAction Action, bool Swap, bool End)>();
            // Cooldown grants held while no cooldown ran (Skilled's banked reset), each distinct one once, with the first cooldown start
            // seen since it was held. The earliest is kept, as a slot's next use replaces what it knows of the last.
            var holds = new List<(double Share, double Seconds, double Delay, double Since, double Start)>();
            // The next whole second of the fight, when a selected rule counts seconds (Stand Your Ground, Chemical Agent).
            var nextSecond = secondsUsed && 1 < options.Duration ? 1d : double.PositiveInfinity;
            // Targets cycle through the sequence and the distances; each carries its own full pools and distance.
            var targetCount = 0;
            // A dead target stays until the next arrives, so this tells a kill still being dealt with from the wait after it.
            var groupMovedOn = false;
            var arrival = 0d;
            var available = 0d;
            // Enemies the player has shot and left, by place, and who comes next: with a shooting pattern, the next in turn.
            var left = new Dictionary<int, FightEnemy>();
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
                if (!left.Remove(comingPlace, out var enemy))
                {
                    targetCount++;
                    var profile = sequence.IsEmpty ? -1 : sequence[comingPlace % sequence.Length];
                    var (fullArmor, fullHealth) = profile < 0 ? (armor, health) : sequencePools[profile];
                    enemy = new(comingPlace, profile, distances.IsEmpty ? options.Distance : distances[comingPlace % distances.Length], fullArmor, fullHealth, available,
                        incoming?.Attackers ?? 1, profiles.Keys, NewDots());
                }
                HoldState(enemy, available);
                if (options.FocusSeconds is { } focus) { moveAt = available + focus; }
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
            void RecordRepair(double armorAmount, double healthAmount, double at)
            {
                FlushRegeneration();
                AddRepair(armorAmount, healthAmount);
                if (armorAmount > 0) { Record(DummyEventKind.ArmorRepaired, armorAmount, at, ""); }
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
                statuses!.Fire(name, at, factor, eligible ?? holdingTalents[hand], gates: gates ?? (gated ? buffs!.Gates(at) : null), distance: target.Distance);
            // weapon is the slot whose weapon produced the event; null for events with no weapon behind them.
            bool FireBuffs(string name, double at, IReadOnlySet<string> active, IReadOnlySet<string>? eligible = null,
                IReadOnlySet<string>? owners = null, string? weapon = null)
            {
                var requested = buffs!.Fire(name, at, active, eligible ?? holdingTalents[hand], owners ?? holdingTalents[hand],
                    weapon is null ? null : weaponTags[weapon], player is null || player.MaxArmor <= 0 ? null : player.Armor / player.MaxArmor,
                    buffs.ReadsStats(name) ? StatsBefore(weapon ?? hand, at) : null);
                counts?.Sample(name, eligible ?? holdingTalents[hand], targeted && target.Dead && !groupMovedOn, at, present);
                // Every event passes here, so a talent debuff (Toxic Delivery) starts on its event on a living target.
                target.Dots?.StartTalents(name, at, eligible ?? holdingTalents[hand], statusTicks, !targeted || !target.Dead);
                ApplyGrants(at);
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
                    fightKillSeconds += at - target.ArrivedAt;
                    if (++fightKills == options.ClearKills) { clearTimes.Add(at); }
                }
                if (slot is null) { lastWeaponKills[hand] = at; }
                if (fight == 0 && killTimes.Count < TraceLimit) { killTimes.Add(at - target.ArrivedAt); }
                else if (fight == 0) { truncated = true; }
                Record(DummyEventKind.Kill, at: at, slot: slot);
                // Every kill drops a pickup when a rule collects them: picked up after the delay, or missed.
                if (pickupsUsed && at < options.Duration && pickupRandom.NextDouble() < options.PickupChance) { pickups.Enqueue(at + options.PickupDelay); }
                repairs();
                var moving = options.FocusSeconds is null ? null : group!.Following(target.Place);
                if (away && comingPlace != target.Place) { return; }
                // After the last enemy no target ever arrives, so skills stop and the fight runs out its length.
                noTargetsLeft = options.FocusSeconds is null ? targetCount >= enemyTotal : moving is null;
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
            void ScheduleAction(WeaponAction action)
            {
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
            }
            // A trace already cut short stays as it is, so it can't reopen with a gap in it.
            void Unrecord(DummyEventKind kind, double at, string slot)
            {
                if (!options.CaptureFirstFightEvents || fight != 0 || truncated) { return; }
                for (var index = events.Count - 1; index >= 0; index--)
                {
                    if (events[index].Kind == kind && events[index].Time == at && events[index].WeaponSlot == slot) { events.RemoveAt(index); return; }
                }
            }
            // A dead target's statuses and the pellets that landed on it never carry to the next one.
            void ClearTarget(double at)
            {
                // The target's death reaches every talent in the fight, after its kill events and however it died (R41: Doctor
                // Home's kit; a mark consumed here clears with its target).
                if (buffs is not null && at < options.Duration && FireBuffs("target_death", at, statuses!.Active(at), fightTalents, fightTalents)) { Refill(at); }
                foreach (var slot in profiles.Keys) { target.PelletHits[slot] = 0; }
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
                    rotation.Refill(hand);
                }
                else if (!initialWeapon.NoReload && !reloadCompleting) { SetAmmo(buffWeapon.Magazine, at); }
            }
            void RefreshWeapon(double at)
            {
                if (buffs is null) { return; }
                var phaseContext = OnTarget(rotation is null ? phases!.Context(context, at, ammo, initialWeapon.Magazine) : HandPhases(hand, at));
                heldMagazine?.Observe(phaseContext, Math.Min(at, options.Duration));
                // While waiting for the next target there is none to read; it arrives at full pools, never 0%.
                var distanceNow = DistanceNow();
                phaseContext = combat!.Context(phaseContext, distanceNow ?? 0, target.PelletHits[hand], targeted && !target.Dead ? target.RemainingPercent() : 100);
                phaseContext = statuses!.Context(phaseContext, at);
                var changed = InHand(buffs.Context(phaseContext, at), hand);
                var state = StateAt(changed, distanceNow);
                if (state != lastState) { Resolve(state, changed, distanceNow); }
                target.Dots?.Sync(statuses, statusTicks);
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
                var read = phases!.Context(handContexts[slot], at, rotation!.ReadableRounds(slot, at), profiles[slot].Magazine);
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
                var phaseContext = OnTarget(rotation is null ? phases!.Context(context, at, ammo, initialWeapon.Magazine) : HandPhases(slot, at));
                phaseContext = combat!.Context(phaseContext, DistanceNow() ?? 0, target.PelletHits[slot], targeted && !target.Dead ? target.RemainingPercent() : 100);
                return InHand(buffs!.Context(statuses!.Context(phaseContext, at), at), slot);
            }
            // A skill reads the build as it deploys, or at each batch for a live rate, with the weapon in hand (ADR 0012). A use keeps the rest.
            SkillDamageSchedule SkillsInState(string state, ImmutableDictionary<string, string> changed)
            {
                if (skillStates.TryGetValue(state, out var cached)) { return cached; }
                SkillDamageSchedule Read(ImmutableDictionary<string, string> at)
                {
                    var dynamic = BuildResolver.Resolve(pack, build, at);
                    if (dynamic.Diagnostics.Any(d => d.Severity == "error")) { throw new SimulationException("resolve-failed", "Triggered state failed resolution"); }
                    return SkillDamageSchedule.Create(pack.Pack, dynamic.ActiveSkillPreviews, options.IncludeShield);
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
                    var profile = ShotProfile.Read(dynamic, targeted, distance);
                    var held = profiles[slot];
                    // Fire rate and reload time are read as each interval or reload starts (Measured, Frenzy, On Empty);
                    // magazine size, pellets and target pools would change state already committed.
                    var stateProfile = sequence.IsEmpty ? -1 : int.Parse(changed[TargetProfileKey], CultureInfo.InvariantCulture);
                    var (stateArmor, stateHealth) = stateProfile < 0 ? (armor, health) : sequencePools[stateProfile];
                    if (profile.Magazine != held.Magazine || profile.NoReload != held.NoReload || profile.Pellets != held.Pellets ||
                        targeted && (dynamic.Calculation.Stats.GetValueOrDefault("enemy_target_armor") != stateArmor || dynamic.Calculation.Stats.GetValueOrDefault("enemy_target_health") != stateHealth))
                    { throw new SimulationException("buff-timing-unsupported", "Triggered magazine, pellet or target-pool changes need dynamic scheduling"); }
                    var dynamicEnemy = incoming is not null ? EnemyFireSchedule.Create(dynamic) : null;
                    RecoveryProfile? dynamicRecovery = null;
                    if (dynamicEnemy is not null)
                    {
                        dynamicRecovery = ReadRecovery(dynamic);
                        // A state's fire must match its target's (the chosen one's without a sequence): triggers may not change it.
                        if (!(stateProfile < 0 ? incoming! : sequenceFire[stateProfile]).SameClockAndPools(dynamicEnemy, hitRateFollows))
                        { throw new SimulationException("incoming-state-unsupported", "Triggered enemy cadence, hit probability or player capacity changes require dynamic scheduling"); }
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
            if (skills is not null && (buffs is not null || rotation is not null)) { skills.Read = (slot, at) => SkillsAt(at).For(slot); }
            if (rotation is not null && options.DriveSimpleBuffs)
            {
                rotation.DrivenTiming = slot =>
                {
                    var profile = HeldProfile(slot, time);
                    return (profile.Reload, profile.SwapSpeed, profile.SwitchMultiplier);
                };
            }
            while (time < options.Duration || skills?.Next is not null || nextEnemy < options.Duration || Ticking()?.Dots!.NextTick < options.Duration ||
                pickups.Count > 0 && pickups.Peek() < options.Duration || actionEvents.Count > 0 && actionEvents.Peek().At < options.Duration || nextSecond < options.Duration ||
                HeldDue() < options.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    if (cycle?.Behind(at) == true)
                    {
                        blockedShots++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    // Only an enemy shot at can be shocked, and its own fire is what shock stops.
                    var firing = group is null ? target : EnemyAt(group.Firing);
                    if (firing?.Shock.Blocks(firing.Statuses.Has("shocked", at)) == true)
                    {
                        blockedShots++;
                        Record(DummyEventKind.EnemyShotBlocked, at: at, slot: "");
                        AdvanceEnemy(at);
                        continue;
                    }
                    // Peeking from cover, a share of the normal hit chance lands.
                    var shooter = Shooter();
                    var landed = roll < shooter.HitProbability * (options.PeekHitShare ?? 1);
                    if (landed)
                    {
                        enemyHits++;
                        if (shield?.IsUp == true)
                        {
                            FlushShieldRepair();
                            var shieldHit = shield.Apply(shooter.RawDamagePerHit, at);
                            shieldAbsorbed[fight] += shieldHit.Absorbed;
                            if (!double.IsFinite(shieldAbsorbed[fight])) { throw new SimulationException("damage-overflow", "Shield absorption total overflowed"); }
                            Record(DummyEventKind.ShieldHit, shieldHit.Absorbed, at, shieldProfile!.Slot);
                            if (shieldHit.Absorbed > 0 && recoveryProfile!.MakeshiftShare > 0)
                            { queuedRepairs.Enqueue(shieldHit.Absorbed * recoveryProfile.MakeshiftShare, recoveryProfile.MakeshiftDuration, at); }
                            if (shieldHit.Broken)
                            {
                                shieldBreaks++;
                                shieldReturn = shieldWeapon is not null;
                                Record(DummyEventKind.ShieldBreak, at: at, slot: shieldProfile.Slot);
                                if (buffs is not null)
                                {
                                    FireStatuses("shield_break", at, buffWeapon.StatusDurationFactor);
                                    var shieldRefill = FireBuffs("shield_break", at, statuses!.Active(at));
                                    if (shieldRefill) { Refill(at); }
                                    RefreshWeapon(at);
                                }
                            }
                            AdvanceEnemy(at);
                            continue;
                        }
                        var hit = player!.Apply(shooter.DamagePerHit, at, recoveryProfile!.BreakArmor);
                        if (!hit.Downed && hit.DamageTaken > 0 && recoveryProfile.MakeshiftShare > 0)
                        { queuedRepairs.Enqueue(hit.DamageTaken * recoveryProfile.MakeshiftShare, recoveryProfile.MakeshiftDuration, at); }
                        incomingDamage[fight] += hit.DamageTaken;
                        if (!double.IsFinite(incomingDamage[fight])) { throw new SimulationException("damage-overflow", "Incoming damage overflowed"); }
                        Record(DummyEventKind.EnemyHit, hit.DamageTaken, at, "");
                        if (kits?.Hit(at) is { } kitEnds) { time = kitEnds; }
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
                            // Arrival traces may have been scheduled before the player went down.
                            for (var index = events.Count - 1; fight == 0 && index >= 0; index--)
                            { if (events[index].Time > at) { events.RemoveAt(index); } }
                            break;
                        }
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
                    }
                    else
                    {
                        var name = ending ? "reload_end" : "reload";
                        FireStatuses(name, at, buffWeapon.StatusDurationFactor);
                        refilled = FireBuffs(name, at, statuses!.Active(at), weapon: action.To);
                    }
                    if (refilled) { Refill(at); }
                    RefreshWeapon(at);
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
                    // A status applied on the second lands only on a living target; between targets there is none.
                    var applied = statuses!.Fire("second", at, buffWeapon.StatusDurationFactor, holdingTalents[hand], !targeted || !target.Dead,
                        gated ? buffs!.Gates(at) : null, target.Distance);
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
                        _ => throw new SimulationException("status-damage-invalid", $"Status {status} has no tick event")
                    }, dealt, at, "");
                    if (targeted && ticking.Dead)
                    {
                        // A kill on an enemy the player left runs as any status kill, on that enemy, and the player then carries on.
                        var (shot, movedOn, away) = (target, groupMovedOn, !ReferenceEquals(ticking, target));
                        if (away)
                        {
                            // The enemy being shot waits with those left, so a count read on this kill still finds it.
                            left.Remove(ticking.Place);
                            if (!shot.Dead) { left[shot.Place] = shot; }
                            target = ticking;
                            groupMovedOn = false;
                            HoldState(target, at);
                        }
                        // Preservation and weapon repair-per-kill are not granted: whether a status kill credits them is an in-game check
                        // (R41). Doctor Home's kit is, when the target was marked.
                        CreditKill(at, "", () => RepairOnKill(at, weaponBound: false), away);
                        if (!away && options.Rotation?.Mode == WeaponSwapMode.AfterKill) { externalKillPending = true; }
                        // A status kill is a kill to gear talents only, as a skill's is. It's no skill_kill: by the source a burn or bleed
                        // tick isn't a skill kill (R77), though a skill's own status ticks in its stream count as one, a known partial.
                        FireStatuses("kill", at, buffWeapon.StatusDurationFactor, gearTalents);
                        if (FireBuffs("kill", at, statuses!.Active(at), gearTalents))
                        { Refill(at); }
                        ClearTarget(at);
                        if (away)
                        {
                            if (!shot.Dead) { left.Remove(shot.Place); }
                            target = shot;
                            groupMovedOn = movedOn;
                            HoldState(target, at);
                        }
                    }
                    RefreshWeapon(at);
                    continue;
                }
                if (nextSkill is not null && (nextSkill.Time <= time || time >= options.Duration) && nextSkill.Time <= nextPickup)
                {
                    // A read, with nothing to deal, so it costs no work and moves nothing.
                    if (nextSkill.Kind == SkillDamageKind.Deployment) { skills!.Deploy(); continue; }
                    Spend();
                    var entry = skills!.Take();
                    var skillStateRefreshed = Regenerate(entry.Time);
                    if (entry.Time < options.Duration && !skillStateRefreshed) { RefreshWeapon(entry.Time); }
                    if (targeted && target.Dead)
                    {
                        if (entry.Time < available) { continue; }
                        target = NewTarget();
                        arrival = available;
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
                    var dealt = targeted ? target.Apply(rawSkillDamage, skillArmorFactor, 1) : rawSkillDamage;
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
                        if (skillStatusApplied && !(targeted && target.Dead)) { EmitSkillEvent("status_applied"); }
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
                    if (ammo >= 1) { continue; }
                    reloadCompleting = true;
                    time = StartReload();
                    continue;
                }
                if (reloadCompleting)
                {
                    reloadCompleting = false;
                    SetAmmo(initialWeapon.Magazine, time);
                    RefreshWeapon(time);
                    Record(DummyEventKind.ReloadCompleted);
                    FinishReload(time);
                    continue;
                }
                // A kit starts at the weapon's next decision, if armor is still low then, so a reload or swap under way finishes
                // first. With a cover cycle it waits for cover: the weapon's clock stops at each boundary, so a decision comes there.
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
                        else if (!buffWeapon.NoReload && ammo < buffWeapon.Magazine)
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
                var healthBefore = target.Health;
                var unarmoredBefore = !targeted || target.Armor == 0;
                double damage;
                var criticalDamage = 0d;
                if (options.Mode == SimulationMode.Random)
                {
                    damage = 0;
                    var landed = 0;
                    // A ready forced-headshot rule (Resolved) makes this shot's first landed pellet a headshot; the shot's own
                    // event then spends it. The random draws are unchanged, so runs without such a rule keep their sequence.
                    var forceHead = buffs?.ForcesHeadshot(time, holdingTalents[hand], weaponTags[hand]) == true;
                    for (var pellet = 0; pellet < (int)weapon.Pellets; pellet++)
                    {
                        Spend();
                        if (random.NextDouble() >= weapon.Hit) { continue; }
                        hitEvent = true;
                        landed++;
                        var critical = random.NextDouble() < weapon.Crit;
                        var headshot = random.NextDouble() < weapon.Head || forceHead;
                        forceHead = false;
                        critEvent |= critical;
                        headEvent |= headshot;
                        bodyEvent |= !headshot;
                        var raw = (headshot ? critical ? weapon.CritHeadDamage : weapon.HeadDamage : critical ? weapon.CritDamage : weapon.BodyDamage) / weapon.Pellets;
                        var alive = !targeted || !target.Dead;
                        if (alive)
                        {
                            hitPellets++;
                            if (critical) { critPellets++; }
                        }
                        if (!targeted) { damage += raw; continue; }
                        var dealt = target.Apply(raw, weapon.ArmorFactor, weapon.HealthFactor);
                        damage += dealt;
                        if (critical) { criticalDamage += dealt; }
                        if (alive && target.Dead) { killingCrit = critical; killingHeadshot = headshot; }
                    }
                    target.PelletHits[hand] = landed;
                }
                else { damage = targeted ? target.Apply(weapon.Expected, weapon.ArmorFactor, weapon.HealthFactor) : weapon.Expected; }
                // A shot is unarmored when its damage reaches health, including the shot that strips the last armor.
                var unarmored = unarmoredBefore || target.Health < healthBefore;
                total += damage;
                weaponTotal += damage;
                timeline[(int)time] += damage / fights;
                if (!double.IsFinite(total) || !double.IsFinite(timeline[(int)time]))
                { throw new SimulationException("damage-overflow", "Simulation damage overflowed"); }
                shots++;
                Record(DummyEventKind.Shot, damage);
                if (player is not null && criticalDamage > 0 && recoveryProfile!.CriticalDamageShare > 0)
                {
                    // Saturating an overflowing product is exact after the finite missing-armor cap.
                    var amount = Math.Min(double.MaxValue, criticalDamage * recoveryProfile.CriticalDamageShare);
                    var repair = player.Repair(amount, 0, time);
                    RecordRepair(repair.Armor, 0, time);
                }
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
                if (rotation is not null)
                {
                    ScheduleAction(rotation.Fire(time, targeted && target.Dead, refill, weapon.Interval, SwapAllowed(), coverStart));
                    continue;
                }
                if (!weapon.NoReload)
                {
                    SetAmmo(refill ? buffWeapon.Magazine : ammo - 1, time);
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
                    SetAmmo(weapon.Magazine, time);
                    if (time < options.Duration) { Record(DummyEventKind.ReloadCompleted); }
                    // Nothing can land during this reload, so its completion fires at once.
                    FinishReload(time);
                }
            }
            Regenerate(options.Duration);
            FlushRegeneration();
            FlushShieldRepair();
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
            if (fightKills > 0) { killTimeMeans.Add(fightKillSeconds / fightKills); }
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
        return new DummyFightResult(options.IncludeIncoming ? "d2p-incoming-random-v73/" + SimulationRandom.Contract : options.DriveSimpleBuffs ? "d2p-simple-buffs-v61/" + SimulationRandom.Contract : options.IncludeSkills ? "d2p-static-skills-v6/" + SimulationRandom.Contract : template is not null ? "d2p-weapon-rotation-v1/" + SimulationRandom.Contract : targeted ? "d2p-static-target-v1/" + SimulationRandom.Contract : Contract, options.Mode, fights, options.Duration, options.Seed, Summarize(dps), shots / fights,
            reloads / fights, [.. timeline], [.. events.OrderBy(entry => entry.Time)], truncated, diagnostics,
            [options.IncludeIncoming ? "Incoming fire: random targets" + (template is not null ? " with driven weapon swaps (recovery follows the weapon in hand)" : "") + "; other queued repairs, shield swaps without a weapon rotation and triggered cadence or capacity changes are unsupported" : targeted ? "Static targets: no incoming damage or repairs" : "Unlimited dummy: no target pools, kills, incoming damage or repairs",
             options.IncludeShield ? "One frontal shield stays raised with static health, active regeneration and break cooldown; holstering, flanking and manual deployment are excluded" +
                 (shieldWeapon is not null ? $". The shield allows only the {pack.Pack.Slots.FirstOrDefault(slot => slot.ID == shieldWeapon)?.Name ?? shieldWeapon}: a raised shield swaps to it at the next shot or after the current reload (the barred weapon may hold the shield until then), and after a break the build's weapon returns the same way" : "") : "Shield absorption is excluded",
             options.IncludeSkills ? "Skills read the build as each deploys, and a use keeps its amounts and cooldown unless its rate is live, with up to 100 ms continuous-damage batches" + (perTargetFire ? ", not split at enemy attempts, which follow the " + (options.EnemiesAtOnce is null ? "target in play" : "enemies in play") : " split at incoming attempts") + "; a use's impacts land together unless its rule spaces them, and one due with no enemy there is lost. In driven fights a skill's burn or bleed reads as on its enemy until the use's window ends, or for one with its own rate until that runs out, with no share in target status uptime. A talent's status starts no ticks while a skill's rate is that status's damage, and ticks as usual after, and the skill's deals nothing while a talent's runs. A use reads a waiting bonus only if it was up as the use deployed; other skill statuses are not modeled" : options.IncludeShield ? "Damaging skills are excluded; the selected shield is simulated" : "Skills are excluded",
             options.DriveSimpleBuffs && template is not null ? "Selected triggers follow weapon swaps and whole seconds; swap_after_kill requires the source weapon's own kill within 10 seconds. A triggered fire rate paces the next interval; triggered reload times and swap speeds apply at each swap or reload decision, with the drawn weapon read as it would be in hand. Magazine readings follow the weapon in hand and are unset for a holstered owner. A talent on both weapons runs one copy per weapon, reported as key@slot; with status rules, holstered effects or keys shared with another talent it is refused. Copied talents with their own combat rules and refills before queued empty actions start are unsupported" :
             options.DriveSimpleBuffs ? "Selected triggers include whole-second, skill-hit and gear skill-kill events; a triggered fire rate or reload time applies from the next interval or reload. Changing magazine size or pellets, swaps and copied talents with their own combat rules are unsupported" : "Assumptions remain fixed; pack triggers, statuses and timers are not advanced",
             !dotRules.IsEmpty ? (statusArmorBonuses.IsEmpty ? "Burn and bleed ticks deal the pack's status damage to target pools without armor or health factors" :
                 "Burn and bleed ticks deal the pack's status damage to target pools, with the pack's armor bonus on armor and no health factor") + "; a status kill credits gear talents, generic kill recovery and target_death rules (Doctor Home's kit), not weapon repair-per-kill or weapon talents' kill rules (Preservation)" :
                 "Status damage over time is excluded unless the pack defines it for a status a selected talent applies",
             !talentDamageRules.IsEmpty ? "Talent debuffs (Toxic Delivery, and Afterburn's single tick) spread their resolved total over equal ticks to the current target's pools without armor or health factors; one runs per target and its event neither restarts nor stacks it, and a kill by one counts as a status kill" :
                 "Talent damage over time is excluded unless the pack defines it for a selected talent",
             options.IncludeIncoming ? "Clutch, Doctor Home and Preservation repair through their pack rules (R43), only when talent triggers are driven; overlapping timed repairs run as independent, uncapped instances" :
                 "Talent repairs from pack rules need incoming fire",
             "Shot-bonus, returned-round and dynamic magazine-capacity mechanics are rejected",
             .. distanceNotes,
             .. (distances.IsEmpty ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
                 $"Targets cycle through {distances.Length} distances ({string.Join(", ", distances.Distinct().Select(meters => meters.ToString("0.##", CultureInfo.InvariantCulture)))} m), each setting its falloff, its distance rules and any enemy fire that follows distance")]),
             .. (armorKit is null ? Array.Empty<string>() : [string.Create(CultureInfo.InvariantCulture,
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
             .. (kitlessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight carries no armor kits, so these read your setting and never react to one: {string.Join(", ", kitlessTalents)}"]),
             .. (skilllessTalents.IsEmpty ? Array.Empty<string>() : [$"This fight has no skills, so these never react to a skill's hit or kill: {string.Join(", ", skilllessTalents)}"]),
             .. (enemyCounts is null ? Array.Empty<string>() :
                 ["Talents that count enemies read those alive in the group, by each enemy's distance. Enemies don't move, so a count changes only as they die or join; one read on an event holds until that event comes round again, starting from none"]),
             .. (uncountedKeys.IsEmpty ? Array.Empty<string>() : [$"With one enemy at a time these stay as set: {string.Join(", ", uncountedKeys)}. Fights count enemies with several at once"]),
             .. (options.FocusSeconds is { } focusSeconds ? [string.Create(CultureInfo.InvariantCulture,
                 $"Assumed shooting pattern: {focusSeconds:0.##} s on an enemy, then on to the next in turn, taking {retargetDelay:0.##} s each move. Enemies keep what's on them, and burns and bleeds keep ticking; time to kill runs from first reaching an enemy, time away included")] : Array.Empty<string>()),
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
            StatusDps = statusDps is null ? null : Summarize(statusDps),
            TalentDps = talentDps is null ? null : Summarize(talentDps),
            TotalDps = Summarize(totalDps),
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
                ArmorRepaired = Mean(armorRepairs),
                HealthRepaired = Mean(healthRepairs),
                ArmorKitsUsed = kitsUsed / fights,
                ArmorKitsCancelled = kitsCancelled / fights,
                ArmorKitRepaired = kitRepaired / fights,
                ShieldAbsorbed = Mean(shieldAbsorbed),
                ShieldBreaks = shieldBreaks / fights,
                ShieldDeployments = shieldDeployments / fights,
                ShieldRepaired = Mean(shieldRepairs),
                ShieldSwitches = shieldSwitches / fights
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

    /// <summary>The name of a rule's talent or specialization node.</summary>
    private static string RuleOwnerName(GamePack pack, string id) => pack.Talents.FirstOrDefault(talent => talent.ID == id)?.Name ??
        pack.Specializations.SelectMany(specialization => specialization.Nodes).First(node => node.ID == id).Name;

    /// <summary>Recovery-named stats that are not in-fight player recovery: skill repair scaling (skills are excluded
    /// from incoming fights), healing given to others, durations rather than amounts, and previews of repairs that pack
    /// rules make in the fight (Clutch, Preservation and Doctor Home's kit, R43).</summary>
    internal static readonly ImmutableHashSet<string> NotPlayerRecovery = ImmutableHashSet.Create(StringComparer.Ordinal,
        "repair_skills", "effective_repair_skills", "total_skill_repair", "outgoing_healing", "nanite_healing_efficiency", "makeshift_repairs_duration",
        "missing_armor_repair_on_crit_percent", "estimated_armor_repair_on_crit", "armor_repair_on_kill_over_time_percent",
        "estimated_armor_repair_on_kill_over_time", "doctor_home_kit_repair_percent");

    private sealed record RecoveryProfile(double KillArmor, double KillHealth, double BreakArmor, double ArmorPerSecond, double CriticalDamageShare, double MakeshiftShare, double MakeshiftDuration, double WeaponKillArmor);
    private static readonly ImmutableHashSet<string> SupportedRecovery = ImmutableHashSet.Create(StringComparer.Ordinal,
        "armor_on_kill", "armor_on_kill_percent", "estimated_armor_on_kill", "armor_repair_per_kill_percent", "estimated_armor_repair_per_kill",
        "estimated_kill_repair_per_second", "health_on_kill", "estimated_health_on_kill", "armor_repair_on_depletion_percent", "estimated_armor_repair_on_depletion",
        "armor_regeneration", "armor_regeneration_percent", "estimated_armor_regeneration", "weapon_crit_repair_share", "estimated_crit_repair_per_second",
        "makeshift_repairs_share", "makeshift_repair_budget", "makeshift_repair_average_rate");

    private static RecoveryProfile ReadIncomingRecovery(ResolvedBuild resolved)
    {
        var recovery = resolved.Calculation.Stats.Where(pair => pair.Value != 0 && RecoveryStat.IsMatch(pair.Key) && !NotPlayerRecovery.Contains(pair.Key) && !SupportedRecovery.Contains(pair.Key))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).FirstOrDefault();
        if (recovery is not null) { throw new SimulationException("incoming-repair-unsupported", $"Incoming fire does not yet support {recovery}"); }
        foreach (var id in new[] { "estimated_kill_repair_per_second", "estimated_crit_repair_per_second" })
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
        return new(genericKillArmor + weaponKillArmor, Read("estimated_health_on_kill"), Read("estimated_armor_repair_on_depletion"),
            regeneration, criticalShare, makeshiftShare, makeshiftDuration, weaponKillArmor);
    }

    private sealed record ShotProfile(double Magazine, double Interval, double Reload, bool NoReload, double Hit, double Head, double Crit, double BodyDamage, double HeadDamage, double CritDamage, double CritHeadDamage, double Expected, double Pellets)
    {
        public double ArmorFactor { get; init; } = 1;
        public double HealthFactor { get; init; } = 1;
        public double StatusDurationFactor { get; init; } = 1;
        public double SwapSpeed { get; init; }
        public double SwitchMultiplier { get; init; } = 1;
        /// <param name="distance">The fight distance, for weapon falloff when the pack declares a floor.</param>
        public static ShotProfile Read(ResolvedBuild resolved, bool targeted, double? distance)
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
