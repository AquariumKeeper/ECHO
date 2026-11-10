using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// What clearing stacks or a rule's firing grants at <paramref name="Time"/>: <paramref name="Kind"/> is the pack's grant kind, with
/// <paramref name="Share"/> its amount and <paramref name="Duration"/> its time, for example "temporary_armor" (a share of max armor
/// for that long) or "skill_cooldown" (a share of each cooldown). A cooldown grant with a <paramref name="HoldDelay"/> is held while
/// no cooldown runs, and lands that long after one starts (Skilled).
/// </summary>
public sealed record TriggerGrant(string Kind, double Share, double Duration, double Time, double HoldDelay = 0);

/// <summary>Integrated true/nonzero seconds and, for stack keys, the integral of the live stack count.</summary>
public sealed record TimedTriggerDuration(double Seconds, double? StackSeconds);

/// <summary>
/// Which driven keys were active, and which stack keys were at their cap, at one time: what a gated status rule reads.
/// </summary>
public sealed class TriggerGates
{
    private readonly ImmutableHashSet<string> active;
    private readonly ImmutableHashSet<string> full;

    internal TriggerGates(ImmutableHashSet<string> active, ImmutableHashSet<string> full)
    {
        this.active = active;
        this.full = full;
    }

    public bool Open(string key, bool whileFull) => whileFull ? full.Contains(key) : active.Contains(key);
}

/// <summary>What rules cleared by target_death hold on one enemy while the player is on another.</summary>
public sealed class EnemyMarks
{
    internal Dictionary<int, double> Deadlines { get; } = [];
    internal Dictionary<string, List<double>> Stacks { get; } = new(StringComparer.Ordinal);
}

/// <summary>Timed boolean trigger and stack state. The caller supplies rules selected for the current equipped talent ownership.</summary>
public sealed class TimedBuffState
{
    private readonly ImmutableArray<SimTriggerDefinition> rules;
    // Each rule's stack cap: its max, or a share of its weapon's magazine (maxMagazine).
    private readonly long[] caps;
    // Whether any rule reads another key's state (whileKey), so each event takes a snapshot first.
    private readonly bool gated;
    private readonly Dictionary<string, List<int>> booleanOwners = new(StringComparer.Ordinal);
    private readonly double[] deadlines;
    private readonly double[] lastTriggered;
    // The rules sharing each grouped rule's cooldown, itself included, one array per group (everyGroup); null for the rest.
    private readonly int[]?[] cooldownGroups;
    // A cancelled buff can't be gained again before this time.
    private readonly double[] lockedUntil;
    // Each pooled rule's banked stacks (its bank key), replaced whenever the pool reaches its cap.
    private readonly Dictionary<string, long> banks = new(StringComparer.Ordinal);
    // Grants from cleared stacks, waiting for the caller to apply them.
    private readonly List<TriggerGrant> grants = [];
    private readonly long[] counted;
    // When each counted rule last counted an event; a count with countWindow clears once that long has passed.
    private readonly double[] lastCounted;
    // When each holstered-gain rule last accrued while its weapons were away; NaN while one is in hand.
    private readonly double[] holsteredSince;
    private readonly long[][] alsoCounted;
    private readonly Dictionary<string, List<double>> stacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> pools = new(StringComparer.Ordinal);
    // Every event some rule gains, resets or consumes on; any other event changes nothing.
    private readonly HashSet<string> events = new(StringComparer.Ordinal);
    private readonly HashSet<string> statEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> activeSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> stackSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> measuredAt = new(StringComparer.Ordinal);
    private double clock;
    // The latest time read since the last event, so NextExpiry skips deadlines a read already passed.
    private double observed;
    // Buffs and stacks cleared by target_death mean one enemy (ADR 0011): Doctor Home's mark, Afterburn's and Shakedown's stacks. They go with it.
    // A ready or lockout rule's deadline is the player's cooldown, so it stays.
    private readonly ImmutableArray<int> onEnemy;
    private EnemyMarks held = new();

    /// <param name="magazines">Each weapon talent's magazine size, for maxMagazine caps. Without it those caps are the rule's
    /// max, which only suits a read before any event.</param>
    /// <param name="durations">Stat values at the fight's start, for durationStat and grantAmountStat. Without them such a rule
    /// has no expiry or no amount, which likewise only suits a read before any event.</param>
    /// <summary>The fight's chance stream, a draw in [0, 1) each call, for rules with a <see cref="SimTriggerDefinition.Chance"/>.</summary>
    internal Func<double>? Roll { get; set; }

    public TimedBuffState(IEnumerable<SimTriggerDefinition> rules, IReadOnlyDictionary<string, double>? magazines = null,
        IReadOnlyDictionary<string, double>? durations = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules.Select(rule => rule.DurationStat.Length == 0 || durations is null ? rule : rule with { Duration = StatDuration(rule, durations), DurationStat = "" })
            .Select(rule => rule.GrantAmountStat.Length == 0 || durations is null ? rule : rule with { GrantAmount = StatGrant(rule, durations), GrantAmountStat = "" })];
        gated = this.rules.Any(rule => rule.WhileKey.Length > 0);
        onEnemy = [.. Enumerable.Range(0, this.rules.Length).Where(index => (this.rules[index].ConsumeOn == "target_death" || this.rules[index].ResetOn == "target_death") && this.rules[index].Kind is "buff" or "stacks")];
        caps = new long[this.rules.Length];
        deadlines = new double[this.rules.Length];
        lastTriggered = Enumerable.Repeat(double.NegativeInfinity, this.rules.Length).ToArray();
        var groups = this.rules.Select((rule, index) => (rule, index)).Where(pair => pair.rule.EveryGroup.Length > 0)
            .GroupBy(pair => pair.rule.EveryGroup, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(pair => pair.index).ToArray(), StringComparer.Ordinal);
        // One cooldown: every member has the same, gained not spent. A fight holds only the build's rules, so a group here may have one
        // member (one of Future Perfect's two talents, R73); the pack's validator catches a misspelling across the whole pack.
        if (groups.Values.Any(members => members.Any(member => this.rules[member].Every != this.rules[members[0]].Every || this.rules[member].CooldownFromEnd)))
        { throw new SimulationException("trigger-unsupported", "An everyGroup's rules need one every, none running from its end"); }
        cooldownGroups = [.. this.rules.Select(rule => rule.EveryGroup.Length == 0 ? null : groups[rule.EveryGroup])];
        lockedUntil = new double[this.rules.Length];
        counted = new long[this.rules.Length];
        lastCounted = Enumerable.Repeat(double.NegativeInfinity, this.rules.Length).ToArray();
        holsteredSince = Enumerable.Repeat(double.NaN, this.rules.Length).ToArray();
        alsoCounted = this.rules.Select(rule => new long[rule.Also.Length]).ToArray();
        for (var index = 0; index < this.rules.Length; index++)
        {
            var rule = this.rules[index];
            var isStack = rule.Kind == "stacks";
            var isBuff = rule.Kind == "buff";
            var rest = rule with
            {
                Talent = "",
                Key = "",
                On = "",
                Kind = "",
                Duration = 0,
                Also = [],
                Phases = [],
                Max = isStack ? 0 : rule.Max,
                Gain = isStack ? 0 : rule.Gain,
                Refresh = isStack ? false : rule.Refresh,
                ResetOn = "",
                AlsoResetOn = "",
                ConsumeOn = "",
                Every = 0,
                EveryGroup = "",
                CooldownFromEnd = rule.Kind == "buff" ? false : rule.CooldownFromEnd,
                Count = 0,
                Refill = false,
                Decay = isStack ? 0 : rule.Decay,
                Purge = isStack ? false : rule.Purge,
                Pool = isStack ? "" : rule.Pool,
                PoolMax = isStack ? 0 : rule.PoolMax,
                WhileStatus = "",
                CancelOn = isBuff ? "" : rule.CancelOn,
                CancelFor = isBuff ? 0 : rule.CancelFor,
                Bank = isStack && rule.Pool.Length > 0 ? "" : rule.Bank,
                ClearGrant = isStack ? "" : rule.ClearGrant,
                ClearAmount = isStack ? 0 : rule.ClearAmount,
                ClearDuration = isStack ? 0 : rule.ClearDuration,
                CountWindow = isStack || isBuff ? 0 : rule.CountWindow,
                WeaponTags = [],
                // A forced headshot is spent by the forcing shot's own event, so only ready rules on hit or shot take it.
                ForceHeadshot = rule.Kind == "ready" && rule.On is "hit" or "shot" || isStack && rule.ConsumeOn is "hit" or "shot" ? false : rule.ForceHeadshot,
                GainWhileHolstered = isStack && !rule.WeaponTags.IsDefaultOrEmpty ? 0 : rule.GainWhileHolstered,
                // Only buff and ready rules grant on firing; stacks grant on clearing (clearGrant).
                Grant = rule.Kind is "buff" or "ready" ? "" : rule.Grant,
                GrantAmount = rule.Kind is "buff" or "ready" ? 0 : rule.GrantAmount,
                GrantDuration = rule.Kind is "buff" or "ready" ? 0 : rule.GrantDuration,
                GrantInstances = rule.Kind is "buff" or "ready" ? "" : rule.GrantInstances,
                GrantHoldDelay = rule.Kind is "buff" or "ready" ? 0 : rule.GrantHoldDelay,
                // Buff and ready rules may be gated by another key or the player's armor (Clutch), and take a stat's duration.
                WhileKey = rule.Kind is "buff" or "ready" ? "" : rule.WhileKey,
                WhileFull = rule.Kind is "buff" or "ready" ? false : rule.WhileFull,
                WhileArmorBelow = rule.Kind is "buff" or "ready" ? 0 : rule.WhileArmorBelow,
                WhileStatAtLeast = rule.Kind is "buff" or "ready" ? null : rule.WhileStatAtLeast,
                DurationStat = rule.Kind is "buff" or "ready" && rule.Duration == 0 ? "" : rule.DurationStat,
                // A grant's stat is read as the fight starts; until then its amount is unset.
                GrantAmountStat = rule.Kind is "buff" or "ready" && rule.Grant is not ("" or "armor_kit_refund") && rule.GrantAmount == 0 ? "" : rule.GrantAmountStat,
                ActiveAfter = rule.Kind is "buff" or "ready" && rule.On.Length > 0 && double.IsFinite(rule.ActiveAfter) && rule.ActiveAfter is >= 0 and <= SimTriggerDefinition.MaximumActiveAfter ? 0 : rule.ActiveAfter,
                MaxMagazine = isStack ? 0 : rule.MaxMagazine,
                Chance = rule.Kind is "buff" or "ready" && rule.On.Length > 0 ? 1 : rule.Chance,
                // The fight has already dropped a rule whose requirements its context doesn't meet.
                RequiresContext = rule.Kind is "buff" or "ready" && rule.On.Length > 0 ? null : rule.RequiresContext
            };
            if (rule.Kind is not ("buff" or "stacks" or "ready" or "lockout") || rule.Key.Length == 0 || rule.Talent.Length == 0 ||
                rule.On.Length == 0 && rule.GainWhileHolstered == 0 || !double.IsFinite(rule.GainWhileHolstered) || rule.GainWhileHolstered < 0 ||
                !double.IsFinite(rule.Duration) || rule.Duration < 0 || !rule.Phases.IsEmpty || rest != new SimTriggerDefinition() ||
                !double.IsFinite(rule.Every) || rule.Every < 0 || rule.Count < 0 || rule.EveryGroup.Length > 0 && (rule.Every <= 0 || rule.Kind == "lockout") ||
                rule.WhileStatus.Length > 0 && rule.WhileStatus != "any" && !StatusTriggerState.Names.Contains(rule.WhileStatus) ||
                rule.Also.Any(gain => gain.On.Length == 0 || gain.Gain is < 1 or > 10000 || gain.Count < 0) ||
                !double.IsFinite(rule.Decay) || rule.Decay < 0 || rule.Decay > 0 && (!isStack || !rule.Refresh || rule.Duration == 0) ||
                (rule.Pool.Length == 0 ? rule.PoolMax != 0 : rule.PoolMax <= 0) ||
                !double.IsFinite(rule.CancelFor) || rule.CancelFor < 0 || rule.CancelFor > 0 && rule.CancelOn.Length == 0 ||
                rule.ClearGrant.Length > 0 && !(rule.ClearGrant is "temporary_armor" or "shield_repair" && double.IsFinite(rule.ClearAmount) && rule.ClearAmount is > 0 and <= 1 &&
                    double.IsFinite(rule.ClearDuration) && (rule.ClearGrant == "temporary_armor" ? rule.ClearDuration is > 0 and <= 600 : rule.ClearDuration == 0)) ||
                rule.ClearGrant.Length == 0 && (rule.ClearAmount != 0 || rule.ClearDuration != 0) ||
                rule.Grant.Length > 0 && !((rule.GrantAmountStat.Length > 0 ? rule.GrantAmount == 0 : double.IsFinite(rule.GrantAmount) && rule.GrantAmount > 0 && rule.GrantAmount <= SimTriggerDefinition.MaximumGrantAmount(rule.Grant)) && double.IsFinite(rule.GrantDuration) && rule.Grant switch
                {
                    "temporary_armor" => rule.GrantDuration is > 0 and <= 600 && rule.GrantInstances.Length == 0,
                    "shield_repair" or "missing_armor_repair" or "skill_cooldown" or "skill_cooldown_seconds" => rule.GrantDuration == 0 && rule.GrantInstances.Length == 0,
                    "armor_kit_refund" => rule.On == "armor_kit" && rule.GrantAmount == 1 && rule.GrantDuration == 0 && rule.GrantInstances.Length == 0,
                    "armor_repair" => rule.GrantDuration == 0 ? rule.GrantInstances.Length == 0 : rule.GrantDuration <= 600 && rule.GrantInstances == "independent",
                    _ => false
                }) ||
                rule.Grant.Length == 0 && (rule.GrantAmount != 0 || rule.GrantDuration != 0 || rule.GrantInstances.Length > 0) ||
                rule.GrantHoldDelay != 0 && !(rule.Grant is "skill_cooldown" or "skill_cooldown_seconds" && double.IsFinite(rule.GrantHoldDelay) && rule.GrantHoldDelay is > 0 and <= SimTriggerDefinition.MaximumGrantHoldDelay) ||
                !double.IsFinite(rule.CountWindow) || rule.CountWindow < 0 || rule.CountWindow > 0 && rule.Count == 0 ||
                // A counted reset only restarts the count, so only a consume clears a counted rule's stacks.
                rule.ClearGrant.Length > 0 && (rule.ResetOn.Length == 0 || rule.Count > 0) && rule.ConsumeOn.Length == 0 ||
                isStack && (rule.Max is < 1 or > 10000 || rule.Gain is < 0 or > 10000) ||
                !double.IsFinite(rule.MaxMagazine) || rule.MaxMagazine is < 0 or > 1 ||
                !double.IsFinite(rule.WhileArmorBelow) || rule.WhileArmorBelow is < 0 or > 1 || rule.WhileFull && rule.WhileKey.Length == 0 ||
                (rule.WhileKey.Length > 0 || rule.WhileArmorBelow > 0) && rule.On.Length == 0 || rule.WhileKey.Length > 0 && rule.WhileKey == rule.Key ||
                rule.WhileStatAtLeast is { } statGate && (rule.On.Length == 0 || statGate.Stat.Length == 0 || !double.IsFinite(statGate.Value) || rule.WhileKey.Length > 0 || rule.WhileArmorBelow > 0))
            { throw new SimulationException("trigger-unsupported", "Only buffs, ready/lockout booleans and bounded stacks are supported"); }
            caps[index] = rule.Max;
            if (rule.MaxMagazine > 0 && magazines is not null)
            {
                if (!magazines.TryGetValue(rule.Talent, out var magazine) || !(magazine > 0))
                { throw new SimulationException("trigger-unsupported", "A magazine-sized stack cap needs its talent on a weapon with a magazine"); }
                var share = Math.Floor(magazine * rule.MaxMagazine);
                caps[index] = share >= rule.Max ? rule.Max : Math.Max(1, (long)share);
            }
            events.UnionWith(rule.Also.Select(gain => gain.On).Append(rule.On).Append(rule.ResetOn).Append(rule.AlsoResetOn).Append(rule.ConsumeOn).Append(rule.CancelOn).Where(name => name.Length > 0));
            if (rule.WhileStatAtLeast is not null) { statEvents.UnionWith(rule.Also.Select(gain => gain.On).Append(rule.On)); }
            if (isStack)
            {
                if (booleanOwners.ContainsKey(rule.Key) || !stacks.TryAdd(rule.Key, [])) { throw new SimulationException("trigger-key-conflict", "Stack keys must have one owner rule"); }
                if (rule.Pool.Length > 0)
                {
                    if (!pools.TryGetValue(rule.Pool, out var members)) { pools[rule.Pool] = members = []; }
                    if (members.Count > 0 && this.rules[members[0]].PoolMax != rule.PoolMax)
                    { throw new SimulationException("trigger-pool-conflict", "Rules in a shared pool must declare the same cap"); }
                    members.Add(index);
                    if (rule.Bank.Length > 0 && !banks.TryAdd(rule.Bank, 0)) { throw new SimulationException("trigger-key-conflict", "Bank keys must have one owner rule"); }
                }
            }
            else
            {
                if (stacks.ContainsKey(rule.Key)) { throw new SimulationException("trigger-key-conflict", "A key cannot be both boolean and stack state"); }
                if (!booleanOwners.TryGetValue(rule.Key, out var owners)) { booleanOwners[rule.Key] = owners = []; }
                if (owners.Count > 0 && this.rules[owners[0]].Kind != rule.Kind)
                { throw new SimulationException("trigger-key-conflict", "Shared boolean keys must use the same rule kind"); }
                owners.Add(index);
            }
        }
        if (banks.Keys.Any(key => stacks.ContainsKey(key) || booleanOwners.ContainsKey(key)))
        { throw new SimulationException("trigger-key-conflict", "A bank key cannot also be a rule's key"); }
    }

    /// <summary>
    /// The weapon in hand changed (or the fight starts): holstered-gain rules whose weapon tags it lacks gain from now on,
    /// and the others stop, keeping what they accrued.
    /// </summary>
    public void Hold(double time, IReadOnlySet<string> weaponTags)
    {
        ArgumentNullException.ThrowIfNull(weaponTags);
        CheckTime(time);
        AccrueHolstered(time);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            if (rule.GainWhileHolstered == 0) { continue; }
            var holstered = !rule.WeaponTags.Any(weaponTags.Contains);
            holsteredSince[index] = !holstered ? double.NaN : double.IsNaN(holsteredSince[index]) ? time : holsteredSince[index];
        }
        Advance(time);
    }

    // Each tick of a holstered-gain rule's free-running clock after it was put away, up to this time, adds a stack, as the
    // source's periodic timer does; uptime is integrated up to each tick before its stack is added.
    private void AccrueHolstered(double time)
    {
        for (var index = 0; index < rules.Length; index++)
        {
            var since = holsteredSince[index];
            if (double.IsNaN(since)) { continue; }
            var rule = rules[index];
            // Ticks are whole multiples of the period, computed from their index so they don't drift.
            for (var count = Math.Floor(since / rule.GainWhileHolstered) + 1; count * rule.GainWhileHolstered <= time; count++)
            {
                var tick = count * rule.GainWhileHolstered;
                var live = stacks[rule.Key].Where(expiry => expiry > tick).ToList();
                if (live.Count >= caps[index]) { break; }
                activeSeconds[rule.Key] = activeSeconds.GetValueOrDefault(rule.Key) + StackActiveInterval(rule.Key, stacks[rule.Key], tick);
                stackSeconds[rule.Key] = stackSeconds.GetValueOrDefault(rule.Key) + StackInterval(rule.Key, stacks[rule.Key], tick);
                measuredAt[rule.Key] = tick;
                live.Add(rule.Duration == 0 ? double.PositiveInfinity : tick + rule.Duration);
                stacks[rule.Key] = live;
            }
            holsteredSince[index] = time;
        }
    }

    /// <summary>The enemy whose marks these rules now read and write. Uptime is settled here, so it counts the enemy being shot.</summary>
    public void HoldEnemy(EnemyMarks marks, double time)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var at = Math.Max(time, clock);
        foreach (var index in onEnemy)
        {
            var key = rules[index].Key;
            if (rules[index].Kind == "stacks")
            {
                var next = marks.Stacks.GetValueOrDefault(key, []);
                if (stacks[key].Count > 0 || next.Count > 0)
                {
                    activeSeconds[key] = activeSeconds.GetValueOrDefault(key) + StackActiveInterval(key, stacks[key], at);
                    stackSeconds[key] = stackSeconds.GetValueOrDefault(key) + StackInterval(key, stacks[key], at);
                    measuredAt[key] = at;
                }
                // Always written back: a reload may have emptied what this enemy held.
                held.Stacks[key] = stacks[key];
                stacks[key] = next;
                continue;
            }
            var deadline = marks.Deadlines.GetValueOrDefault(index);
            if (deadlines[index] != 0 || deadline != 0)
            {
                activeSeconds[key] = activeSeconds.GetValueOrDefault(key) + BooleanInterval(key, booleanOwners[key], at);
                measuredAt[key] = at;
            }
            held.Deadlines[index] = deadlines[index];
            deadlines[index] = deadline;
        }
        held = marks;
    }

    /// <summary>
    /// Whether a ready rule with forceHeadshot is ready at this time for a weapon with these item tags, so that weapon's next
    /// landed hit is a headshot. Null eligibility means every rule's talent reacts, as in <see cref="Fire"/>.
    /// </summary>
    public bool ForcesHeadshot(double time, IReadOnlySet<string>? eligibleTalents, IReadOnlySet<string> weaponTags)
    {
        ArgumentNullException.ThrowIfNull(weaponTags);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            // A ready rule forces while ready; a stacks rule (Quick Draw) while it holds stacks.
            var armed = rule.Kind == "stacks" ? stacks[rule.Key].Any(expiry => expiry > time) : time >= deadlines[index];
            if (rule.ForceHeadshot && armed && (eligibleTalents is null || eligibleTalents.Contains(rule.Talent)) &&
                (rule.WeaponTags.IsDefaultOrEmpty || rule.WeaponTags.Any(weaponTags.Contains))) { return true; }
        }
        return false;
    }

    /// <summary>The next effective boolean transition or stack expiry; ready/lockout keys recover at their exact deadline.</summary>
    public double? NextExpiry => booleanOwners.Values.Select(owners => rules[owners[0]].Kind == "buff" ? owners.Max(index => deadlines[index]) : owners.Min(index => deadlines[index])).Concat(stacks.Values.SelectMany(values => values)).Where(until => double.IsFinite(until) && until > Math.Max(clock, observed)).Select(until => (double?)until).Min();

    /// <summary>Applies one event atomically and returns whether an activated rule requests a magazine refill.
    /// eligibleTalents limits gains/cancellation; eventOwners also scopes resets/consumption to the weapon in hand
    /// and gear. With no owner scope, skill kills retain the existing agent-wide reset behavior.</summary>
    /// <summary>Whether a rule on this event is gated on a stat, which is worth resolving for only then.</summary>
    public bool ReadsStats(string eventName) => statEvents.Contains(eventName);

    /// <summary>Events a weapon can produce; a rule with weapon tags reacts to them only from a tagged weapon.</summary>
    internal static readonly ImmutableHashSet<string> WeaponEvents = ImmutableHashSet.Create(StringComparer.Ordinal, "shot", "hit", "hit_pellet", "crit", "crit_pellet", "headshot",
        "body_hit", "unarmored_hit", "kill", "crit_kill", "headshot_kill", "reload", "reload_end", "swap", "swapped", "swap_after_kill", "status_applied");

    /// <param name="armorShare">The player's armor as a share of max armor at the event, for whileArmorBelow; without it
    /// armor is taken as full.</param>
    /// <param name="stats">The build's stats as they stood before the event, for whileStatAtLeast; without them its rules stay
    /// closed. Only an event in <see cref="ReadsStats"/> needs them.</param>
    public bool Fire(string eventName, double time, IReadOnlySet<string>? activeStatuses = null, IReadOnlySet<string>? eligibleTalents = null,
        IReadOnlySet<string>? eventOwners = null, IReadOnlySet<string>? weaponTags = null, double? armorShare = null, IReadOnlyDictionary<string, double>? stats = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        CheckTime(time);
        AccrueHolstered(time);
        if (!events.Contains(eventName)) { Advance(time); return false; }
        // A gated rule reads the keys as they stood before this event.
        var gates = gated ? Gates(time) : null;
        // Stage all per-rule changes so failed expiry validation cannot commit counts or clears.
        var nextDeadlines = (double[])deadlines.Clone();
        var nextLast = (double[])lastTriggered.Clone();
        var nextLocked = (double[])lockedUntil.Clone();
        var bankUpdates = new Dictionary<string, long>(StringComparer.Ordinal);
        var granted = new List<TriggerGrant>();
        var nextCounted = (long[])counted.Clone();
        var nextLastCounted = (double[])lastCounted.Clone();
        var nextAlso = alsoCounted.Select(values => (long[])values.Clone()).ToArray();
        var refill = false;
        var stackUpdates = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            if (eventOwners is not null && !eventOwners.Contains(rule.Talent)) { continue; }
            // Not yet active (Efficient's activation delay): no event gains, counts, clears or cancels it.
            if (time < rule.ActiveAfter) { continue; }
            // A weapon event from a weapon without one of the rule's tags, or with no weapon behind it (a skill or status kill),
            // neither gains nor clears it; events that are never a weapon's (second, damaged…) are not filtered.
            if (!rule.WeaponTags.IsDefaultOrEmpty && WeaponEvents.Contains(eventName) && (weaponTags is null || !rule.WeaponTags.Any(weaponTags.Contains))) { continue; }
            // Counted resets preserve earned activity; consumption preserves a partial count.
            var resets = rule.ResetOn == eventName || rule.AlsoResetOn == eventName;
            if (resets && rule.Count > 0) { nextCounted[index] = 0; }
            // A status landing on the bullet that earned a bonus doesn't spend it: the bonus is for the next one.
            else if (eventName == "status_applied" && !resets && rule.ConsumeOn == eventName && nextLast[index] == time) { }
            else if (resets || rule.ConsumeOn == eventName)
            {
                // The cooldown starts as the bonus goes, only if one was up to spend.
                if (rule.CooldownFromEnd && rule.ConsumeOn == eventName && time < nextDeadlines[index]) { nextLast[index] = time; }
                nextDeadlines[index] = 0;
                Array.Clear(nextAlso[index]);
                if (rule.Kind == "stacks")
                {
                    // Clearing live stacks grants per stack removed (Full Stop's temporary armor, Liberty or Death's shield repair).
                    var removed = stackUpdates.GetValueOrDefault(rule.Key, stacks[rule.Key]).Count(expiry => expiry > time);
                    if (removed > 0 && rule.ClearGrant.Length > 0) { granted.Add(new(rule.ClearGrant, removed * rule.ClearAmount, rule.ClearDuration, time)); }
                    stackUpdates[rule.Key] = [];
                }
            }
            if (eligibleTalents is not null && !eligibleTalents.Contains(rule.Talent)) { continue; }
            // Cancelling takes the event: an active buff ends and locks out; it does not also regain. Only an event
            // credited to the rule's talent cancels it (a status kill is credited to gear talents only).
            if (rule.CancelOn == eventName && time < nextDeadlines[index])
            {
                var locked = time + rule.CancelFor;
                if (!double.IsFinite(locked)) { throw new SimulationException("trigger-time-invalid", "Trigger lockout cannot advance time"); }
                nextDeadlines[index] = time;
                nextLocked[index] = locked;
                continue;
            }
            // A ready rule cannot retrigger or request side effects during its cooldown, nor a bonus waiting to be spent.
            if ((rule.Kind == "ready" || rule.CooldownFromEnd) && time < nextDeadlines[index]) { continue; }
            var gain = rule.On == eventName ? Math.Max(1, rule.Gain) : 0;
            var alternate = -1;
            if (rule.On != eventName)
            {
                for (var candidate = 0; candidate < rule.Also.Length; candidate++)
                {
                    if (rule.Also[candidate].On != eventName) { continue; }
                    gain = rule.Also[candidate].Gain;
                    alternate = candidate;
                    break;
                }
            }
            // A shared cooldown runs from the group's latest firing, this event's included.
            var last = nextLast[index];
            foreach (var member in cooldownGroups[index] ?? []) { last = Math.Max(last, nextLast[member]); }
            if (gain == 0 || time - last < rule.Every || time < nextLocked[index]) { continue; }
            if (rule.WhileStatus.Length > 0 && (activeStatuses is null ||
                !(rule.WhileStatus == "any" ? activeStatuses.Count > 0 : activeStatuses.Contains(rule.WhileStatus)))) { continue; }
            // Either gate opens a rule that has both (Clutch: low armor, or the post-kill flag); a closed rule neither counts nor fires.
            if ((rule.WhileKey.Length > 0 || rule.WhileArmorBelow > 0) &&
                !(rule.WhileKey.Length > 0 && gates!.Open(rule.WhileKey, rule.WhileFull) || rule.WhileArmorBelow > 0 && (armorShare ?? 1) < rule.WhileArmorBelow))
            { continue; }
            if (rule.WhileStatAtLeast is { } statGate && !(stats?.GetValueOrDefault(statGate.Stat) >= statGate.Value)) { continue; }
            // A chance is drawn only for an event that passes the gates; a failed draw neither counts nor fires.
            if (rule.Chance < 1 && (Roll ?? throw new SimulationException("trigger-chance-unsupported", "A rule with a chance needs the fight's chance stream"))() >= rule.Chance) { continue; }
            if (alternate >= 0 && rule.Also[alternate].Count > 0)
            {
                if (++nextAlso[index][alternate] < rule.Also[alternate].Count) { continue; }
                nextAlso[index][alternate] = 0;
            }
            if (rule.Count > 0)
            {
                // A partial count left longer than its window has dropped off (Pummel's drop-off timer finished first).
                if (rule.CountWindow > 0 && nextCounted[index] > 0 && time - nextLastCounted[index] >= rule.CountWindow) { nextCounted[index] = 0; }
                nextLastCounted[index] = time;
                if (++nextCounted[index] < rule.Count) { continue; }
            }
            nextCounted[index] = 0;
            var until = rule.Duration == 0 ? double.PositiveInfinity : time + rule.Duration;
            if (rule.Duration > 0 && (!double.IsFinite(until) || until <= time))
            { throw new SimulationException("trigger-time-invalid", "Trigger expiry cannot advance time"); }
            nextLast[index] = time;
            refill |= rule.Refill;
            // A buff or ready rule's grant comes with each firing (a ready rule on cooldown never gets here).
            if (rule.Grant.Length > 0) { granted.Add(new(rule.Grant, rule.GrantAmount, rule.GrantDuration, time, rule.GrantHoldDelay)); }
            if (rule.Kind != "stacks") { nextDeadlines[index] = until; continue; }
            var values = stackUpdates.GetValueOrDefault(rule.Key, stacks[rule.Key]).Where(expiry => expiry > time).ToList();
            var cap = caps[index];
            if (rule.Purge && values.Count >= cap)
            {
                stackUpdates[rule.Key] = [];
                Array.Clear(nextAlso[index]);
                continue;
            }
            var gained = (int)Math.Min(cap, gain);
            if (rule.Decay > 0)
            {
                var count = Math.Min((int)cap, values.Count + gained);
                values = [];
                for (var stack = 1; stack <= count; stack++)
                {
                    var expiry = until + stack / rule.Decay;
                    if (!double.IsFinite(expiry) || expiry <= (values.Count == 0 ? until : values[^1]))
                    { throw new SimulationException("trigger-time-invalid", "Stack decay deadlines cannot advance time"); }
                    values.Add(expiry);
                }
                stackUpdates[rule.Key] = values;
                continue;
            }
            if (rule.Refresh) { values = [.. values.Select(_ => until)]; }
            var remove = Math.Max(0, values.Count + gained - (int)cap);
            if (remove > 0) { values.RemoveRange(0, remove); }
            values.AddRange(Enumerable.Repeat(until, gained));
            stackUpdates[rule.Key] = values;
        }
        // Pool caps apply after all gains from this event, using only live stacks.
        foreach (var members in pools.Values)
        {
            var total = members.Sum(index => (long)stackUpdates.GetValueOrDefault(rules[index].Key, stacks[rules[index].Key]).Count(expiry => expiry > time));
            if (total < rules[members[0]].PoolMax) { continue; }
            foreach (var index in members)
            {
                if (rules[index].Bank.Length > 0)
                { bankUpdates[rules[index].Bank] = stackUpdates.GetValueOrDefault(rules[index].Key, stacks[rules[index].Key]).Count(expiry => expiry > time); }
                stackUpdates[rules[index].Key] = [];
                nextCounted[index] = 0;
                Array.Clear(nextAlso[index]);
            }
        }
        // Integrate only keys whose deadlines change; ordinary events do not scan live stack lists.
        var changedBooleans = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Length; index++)
        { if (nextDeadlines[index] != deadlines[index] && rules[index].Kind != "stacks") { changedBooleans.Add(rules[index].Key); } }
        foreach (var key in changedBooleans)
        {
            activeSeconds[key] = activeSeconds.GetValueOrDefault(key) + BooleanInterval(key, booleanOwners[key], time);
            measuredAt[key] = time;
        }
        foreach (var key in stackUpdates.Keys)
        {
            activeSeconds[key] = activeSeconds.GetValueOrDefault(key) + StackActiveInterval(key, stacks[key], time);
            stackSeconds[key] = stackSeconds.GetValueOrDefault(key) + StackInterval(key, stacks[key], time);
            measuredAt[key] = time;
        }
        Advance(time);
        nextDeadlines.CopyTo(deadlines, 0);
        nextLast.CopyTo(lastTriggered, 0);
        nextLocked.CopyTo(lockedUntil, 0);
        foreach (var (key, value) in bankUpdates) { banks[key] = value; }
        grants.AddRange(granted);
        nextCounted.CopyTo(counted, 0);
        nextLastCounted.CopyTo(lastCounted, 0);
        for (var index = 0; index < nextAlso.Length; index++) { nextAlso[index].CopyTo(alsoCounted[index], 0); }
        foreach (var (key, values) in stackUpdates) { stacks[key] = values; }
        return refill;
    }

    /// <summary>Grants from stacks cleared since the last call, in event order; the caller applies them to the player or shield.</summary>
    public ImmutableArray<TriggerGrant> TakeGrants()
    {
        if (grants.Count == 0) { return []; }
        var taken = grants.ToImmutableArray();
        grants.Clear();
        return taken;
    }

    /// <summary>
    /// Driven keys override supplied assumptions both before activation and after expiration.
    /// A read: it never moves the event clock, so a later event may still fire at an earlier time.
    /// </summary>
    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> assumptions, double time)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        CheckTime(time);
        observed = Math.Max(observed, time);
        var result = assumptions.ToBuilder();
        foreach (var (key, owners) in booleanOwners)
        {
            if (owners.Any(index => rules[index].Kind == "buff" ? time < deadlines[index] : time >= deadlines[index])) { result[key] = "true"; }
            else { result.Remove(key); }
        }
        foreach (var (key, values) in stacks)
        { result[key] = values.Count(expiry => time < expiry).ToString(CultureInfo.InvariantCulture); }
        foreach (var (key, value) in banks) { result[key] = value.ToString(CultureInfo.InvariantCulture); }
        return result.ToImmutable();
    }

    private static double StatGrant(SimTriggerDefinition rule, IReadOnlyDictionary<string, double> stats)
    {
        var most = SimTriggerDefinition.MaximumGrantAmount(rule.Grant);
        if (rule.Grant is "" or "armor_kit_refund" || rule.GrantAmount != 0 || !stats.TryGetValue(rule.GrantAmountStat, out var amount) || !double.IsFinite(amount) || amount <= 0 || amount > most)
        { throw new SimulationException("trigger-unsupported", $"grantAmountStat {rule.GrantAmountStat} needs a rule without a grantAmount and a value above 0 up to {most}"); }
        return amount;
    }

    private static double StatDuration(SimTriggerDefinition rule, IReadOnlyDictionary<string, double> durations)
    {
        if (rule.Duration != 0 || !durations.TryGetValue(rule.DurationStat, out var duration) || !double.IsFinite(duration) || duration <= 0 || duration > 600)
        { throw new SimulationException("trigger-unsupported", $"durationStat {rule.DurationStat} needs a rule without a duration and a value above 0 up to 600"); }
        return duration;
    }

    /// <summary>The keys active at this time, as <see cref="Context"/> reads them, and the stack keys at their cap. A read.</summary>
    public TriggerGates Gates(double time)
    {
        CheckTime(time);
        observed = Math.Max(observed, time);
        var active = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var full = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var (key, owners) in booleanOwners)
        { if (owners.Any(index => rules[index].Kind == "buff" ? time < deadlines[index] : time >= deadlines[index])) { active.Add(key); } }
        for (var index = 0; index < rules.Length; index++)
        {
            if (rules[index].Kind != "stacks") { continue; }
            var live = stacks[rules[index].Key].Count(expiry => time < expiry);
            if (live > 0) { active.Add(rules[index].Key); }
            if (live >= caps[index]) { full.Add(rules[index].Key); }
        }
        return new(active.ToImmutable(), full.ToImmutable());
    }

    public ResolvedBuild Resolve(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> assumptions, double time)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        ValidatePack(pack);
        return BuildResolver.Resolve(pack, build, Context(assumptions, time));
    }

    /// <summary>Rules and the inputs they drive never change during a run, so callers check them once.</summary>
    public void ValidatePack(CompiledPackExpressions pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        foreach (var rule in rules)
        {
            if (!pack.Pack.Simulation.Triggers.Contains(rule) || !(rule.Kind != "stacks" ? TriggerInputs.IsTrueOnly(pack.Pack.Selectors.FirstOrDefault(selector => selector.ID == rule.Key))
                : pack.Pack.NumericContextInputs.Any(input => input.ID == rule.Key && input.Min == 0 && input.Step == 1 && input.Max >= rule.Max)))
            { throw new SimulationException("trigger-pack-mismatch", "Rules and driven boolean/numeric inputs must belong to the resolved pack"); }
            // A bank holds the rule's live stacks at the cap, which can be up to the rule's own max.
            if (rule.Bank.Length > 0 && !pack.Pack.NumericContextInputs.Any(input => input.ID == rule.Bank && input.Min == 0 && input.Step == 1 && input.Max >= rule.Max))
            { throw new SimulationException("trigger-pack-mismatch", "A bank key must be a whole-number input that holds the rule's max stacks"); }
        }
    }

    /// <summary>Measures each driven key through time without committing the read.
    /// Boolean seconds follow Context (including ready/lockout polarity); stack seconds integrate live counts.</summary>
    public ImmutableDictionary<string, TimedTriggerDuration> Measure(double time)
    {
        CheckTime(time);
        var result = ImmutableDictionary.CreateBuilder<string, TimedTriggerDuration>(StringComparer.Ordinal);
        foreach (var (key, owners) in booleanOwners)
        { result[key] = new(activeSeconds.GetValueOrDefault(key) + BooleanInterval(key, owners, time), null); }
        foreach (var (key, values) in stacks)
        {
            result[key] = new(activeSeconds.GetValueOrDefault(key) + StackActiveInterval(key, values, time),
                stackSeconds.GetValueOrDefault(key) + StackInterval(key, values, time));
        }
        return result.ToImmutable();
    }

    private double BooleanInterval(string key, List<int> owners, double time)
    {
        var since = measuredAt.GetValueOrDefault(key);
        if (rules[owners[0]].Kind == "buff")
        { return Math.Max(0, Math.Min(time, owners.Max(index => deadlines[index])) - since); }
        return Math.Max(0, time - Math.Max(since, owners.Min(index => deadlines[index])));
    }

    private double StackActiveInterval(string key, List<double> values, double time) =>
        Math.Max(0, Math.Min(time, values.DefaultIfEmpty(0).Max()) - measuredAt.GetValueOrDefault(key));

    private double StackInterval(string key, List<double> values, double time) =>
        values.Sum(until => Math.Max(0, Math.Min(time, until) - measuredAt.GetValueOrDefault(key)));

    // An event may land before an earlier read's time, so it restarts the read watermark at the event.
    private void Advance(double time)
    {
        clock = time;
        observed = time;
    }

    private void CheckTime(double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Trigger time must be finite and monotonic"); }
    }
}
