using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>One deployment's resolved impact and continuous active window. A fight puts its status on the enemy; this plan doesn't.</summary>
/// <param name="Repair">The armor each impact repairs on the player <paramref name="RepairDelay"/> later (ADR 0030), as an event of its own; 0
/// for none, or for a repair landing with its impact, which the fight makes there.</param>
/// <param name="FightEnd">The fight's length: a repair due then or later lands nothing.</param>
/// <param name="RepairEvery">A repair skill's seconds between checks of the player's armor through its window (ADR 0030); 0 for none.</param>
public sealed record SkillUse(string Slot, string Skill, double Time, double WindowEnd, double Rate, double Impact, double StatusRate = 0, double StatusEnd = 0,
    double Impacts = 1, double ImpactInterval = 0, bool Control = false, double Repair = 0, double RepairDelay = 0, double FightEnd = double.PositiveInfinity,
    double RepairEvery = 0)
{
    /// <summary>Until when the use puts its status on an enemy: its window, or its last impact if later (Artillery Turret's shells).</summary>
    public double ApplyingEnd => Math.Max(WindowEnd, Time + (Impacts - 1) * ImpactInterval);
}

/// <summary>
/// Damage meaning is separate from timing. StatusDot is the damage of a skill's own burn or bleed (R65). Control is a control skill's use
/// putting its status on (ADR 0017), with no damage. Repair is an impact's repair to the player landing after it (ADR 0030), its amount
/// in the damage, or a repair skill's repair or cloud tick. RepairCheck is a repair skill's check of the player's armor, its
/// <see cref="SkillDamageEvent.Start"/> the use's time. Charge is a buff hive's charge landing (ADR 0031), firing its skill_charge. Pulse
/// is a hive's pulse while holstered (ADR 0031 stage 2c).
/// </summary>
public enum SkillDamageKind { Impact, ContinuousWindow, StatusDot, Deployment, Control, Repair, RepairCheck, Charge, Pulse }

internal static class SkillDamageKinds
{
    /// <summary>Whether damage runs over a stretch of time, not at a moment.</summary>
    public static bool IsBatch(this SkillDamageKind kind) => kind is SkillDamageKind.ContinuousWindow or SkillDamageKind.StatusDot;
}

/// <summary>One contribution from the resolved deployment plan, with its explicit damage classification.</summary>
public sealed record SkillDamageEvent(double Time, double Damage, string Slot, SkillDamageKind Kind, double Start)
{
    public int HitGroup { get; init; }
}

public sealed class SkillDamageSchedule
{
    /// <summary>A slot's skill as read in one state. <paramref name="Amounts"/> holds its impact and rate against each target profile of a mixed fight.</summary>
    /// <param name="Status">The burn or bleed the use puts on each enemy it lands on, if any, until its window ends.</param>
    /// <param name="StatusRate">The status's own damage per second, if it has one apart from the rate.</param>
    /// <param name="StatusWindow">How long that status lasts after its last application, at the end of the window or the last impact.</param>
    /// <param name="Impacts">How many impacts of <paramref name="Impact"/> a use has, spaced apart. Those landing together are one, of their total.</param>
    /// <param name="ImpactInterval">The seconds between them.</param>
    internal sealed record Plan(string Slot, string Skill, double Window, double Cycle, double Rate, double Impact, string RateArmorBonus, bool LiveRate,
        string Status = "", ImmutableDictionary<int, (double Impact, double Rate, double StatusRate)>? Amounts = null, double Stock = 1, double StockInterval = 0,
        double StatusRate = 0, double StatusWindow = 0, string StatusArmorBonus = "", double Impacts = 1, double ImpactInterval = 0)
    {
        /// <summary>The skill's cooldown itself; <see cref="Cycle"/> adds the use's length to it when the cooldown runs from a use's end.</summary>
        public double Cooldown { get; init; } = Cycle;

        /// <summary>The radius, in metres, of each impact's area (ADR 0016); 0 for the enemy being shot alone.</summary>
        public double Area { get; init; }

        /// <summary>Within this radius an area's impact deals full damage; beyond it, falling in a straight line to <see cref="AreaEdge"/> (R107).</summary>
        public double AreaFull { get; init; }

        /// <summary>Whether the area is a cloud's: a status window's with no impact, reaching others while it applies (ADR 0024).</summary>
        public bool Cloud { get; init; }

        /// <summary>The chance a landed impact kills an eligible enemy outright (ADR 0029), its profiles and its delay; 0 for none.</summary>
        public double KillChance { get; init; }
        public ImmutableArray<int> KillProfiles { get; init; } = [];
        public double KillDelay { get; init; }

        /// <summary>Whether impacts stop at the enemy's armor (ADR 0029).</summary>
        public bool ArmorOnly { get; init; }

        /// <summary>Seconds from a deployment to its effects (ADR 0029); the cooldown still counts from the deployment.</summary>
        public double Delay { get; init; }

        /// <summary>The armor each landed impact repairs on the player under enemy fire (ADR 0028); 0 for none.</summary>
        public double ImpactRepair { get; init; }

        /// <summary>Seconds from each impact to its repair (ADR 0030); 0 for a repair with the impact.</summary>
        public double RepairDelay { get; init; }

        /// <summary>The share of maximum armor the player's must be below for the skill to be used (ADR 0030); 0 for no wait.</summary>
        public double UseArmorBelow { get; init; }

        /// <summary>
        /// A repair skill's (ADR 0030): the armor a repair restores, or a cloud's a second; the seconds between checks; from a check to the
        /// repair; the share of maximum armor the player's must be below (1 for below full); the repairs a use holds (infinite for no
        /// count); and a cloud's seconds (0 for a repair at once).
        /// </summary>
        public double Repair { get; init; }
        public double RepairEvery { get; init; }
        public double RepairLag { get; init; }
        public double RepairBelow { get; init; } = 1;
        public double RepairCount { get; init; } = double.PositiveInfinity;
        public double RepairCloud { get; init; }

        /// <summary>Whether each check reads <see cref="Repair"/> from the fight's state then, not as the use deployed (ADR 0031).</summary>
        public bool LiveRepair { get; init; }

        /// <summary>A buff hive's seconds each charge's buff lasts (ADR 0031); 0 for none. Its checks fire charges, with no armor test.</summary>
        public double ChargeFor { get; init; }

        /// <summary>
        /// Whether its charges go to the other slots' running uses (ADR 0031: the Artificer), each target again only <see cref="ChargeFor"/>
        /// after the last charge landed on it, and how many seconds each charge adds to the use it lands on.
        /// </summary>
        public bool ChargeSkills { get; init; }
        public double Refresh { get; init; }

        /// <summary>A skill drawing enemy fire's health (ADR 0034: the Decoy); 0 for none.</summary>
        public double DecoyHealth { get; init; }

        /// <summary>Its pulses while holstered (stage 2c): the seconds between them and from one to its landing; 0 for none.</summary>
        public double HolsteredEvery { get; init; }
        public double HolsteredLag { get; init; }

        /// <summary>The skill's family in the pack (turret, drone, chem_launcher...).</summary>
        public string Family { get; init; } = "";

        /// <summary>Whether the cooldown runs from the use's end, not from the use.</summary>
        public bool CooldownFromEnd { get; init; }

        /// <summary>
        /// Whether a hive charging skills can reach this plan's uses (ADR 0031): a deployed skill with a window of its own, not a control
        /// skill's status window nor another hive's charges. Its window must be what it runs for, so not one with impacts spaced past it,
        /// nor one ending a delay after its last impact. A use marking zones (ADR 0036) leaves nothing out to reach, and one
        /// waiting to revive its user (ADR 0037) gains nothing from a charge.
        /// </summary>
        public bool Reachable => Window > 0 && ChargeFor == 0 && !MarksZones && Revive is null && ControlStatus.Length == 0 && (Impacts - 1) * ImpactInterval <= Window && EndDelay <= Window;

        /// <summary>Whether a use marks zones on the enemy being shot (ADR 0036), the zones' health in all, and their multiplier's factor.</summary>
        public bool MarksZones { get; init; }
        public double ZoneHealth { get; init; }
        public double ZoneFactor { get; init; } = 1;

        /// <summary>A use's self-revive (ADR 0037), with the share of armor it restores and the seconds it takes; null for none.</summary>
        public SimSkillRevive? Revive { get; init; }
        public double ReviveArmor { get; init; }
        public double ReviveSeconds { get; init; }

        /// <summary>The end delay after a repair skill's last repair, when it holds a count and its cooldown runs from its end.</summary>
        public double EndDelay { get; init; }


        /// <summary>The most distinct enemies a use's impact strikes, the enemy being shot first (ADR 0027); 1 for that one alone.</summary>
        public int Targets { get; init; } = 1;

        /// <summary>A control skill's status, put on as the skill is used (ADR 0017); empty for none.</summary>
        public string ControlStatus { get; init; } = "";

        /// <summary>Whether the control status's area is measured from the player (ADR 0035).</summary>
        public bool AreaFromPlayer { get; init; }

        /// <summary>The share of the impact at the area's radius; 1 for no falloff.</summary>
        public double AreaEdge { get; init; } = 1;

        /// <summary>
        /// The share of the impact an enemy this far from the enemy being shot takes. A full-damage radius at or past the area's own, as a
        /// Seeker's fixed 1 m can be when mods shrink its radius, leaves the whole area at full damage, as the source's would be.
        /// </summary>
        public double AreaShare(double distance) => AreaEdge >= 1 || distance <= AreaFull || Area <= AreaFull ? 1
            : Math.Max(AreaEdge, 1 - (1 - AreaEdge) * (distance - AreaFull) / (Area - AreaFull));

        /// <summary>The use deployed at this time, in a fight of this length, whose impacts end with it.</summary>
        public SkillUse UseAt(double time, double duration)
        {
            // A delayed use's effects all land that much later (ADR 0029); one landing after the fight lands nothing.
            time += Delay;
            var impacts = time >= duration ? 0 : ImpactsBefore(time, duration);
            // Each impact puts the status on, so it applies until the last and runs its own window after that.
            var applying = Math.Max(Window, (impacts - 1) * ImpactInterval);
            return new(Slot, Skill, time, Math.Min(duration, time + Window), Rate, Impact, StatusRate, Math.Min(duration, time + applying + StatusWindow), impacts, ImpactInterval,
                ControlStatus.Length > 0, RepairDelay > 0 ? ImpactRepair : 0, RepairDelay, duration, RepairEvery);
        }

        // The division can round up, and an impact at the fight's very end has no timeline bucket.
        private double ImpactsBefore(double time, double duration)
        {
            if (Impacts <= 1) { return 1; }
            var count = Math.Min(Impacts, Math.Ceiling((duration - time) / ImpactInterval));
            return count > 1 && time + (count - 1) * ImpactInterval >= duration ? count - 1 : count;
        }

        /// <summary>When the next use comes after one at this time.</summary>
        public double After(double time)
        {
            var next = time + Cycle;
            if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "Skill cycle cannot advance time"); }
            return next;
        }
    }
    private readonly ImmutableArray<Plan> plans;
    // Read for every batch of every fight, so held by slot.
    private readonly ImmutableDictionary<string, string> rateArmorBonuses;

    private SkillDamageSchedule(ImmutableArray<Plan> plans, ImmutableDictionary<string, SimSkillAside>? aside = null)
    {
        this.plans = plans;
        Aside = aside ?? ImmutableDictionary<string, SimSkillAside>.Empty;
        rateArmorBonuses = plans.ToImmutableDictionary(plan => plan.Slot, plan => plan.RateArmorBonus, StringComparer.Ordinal);
    }

    /// <summary>
    /// The slots whose skill steps aside, never used in the fight: by the pack's list (ADR 0022), or because its rule's
    /// <see cref="SimSkillDefinition.RequiresContext"/> isn't held (ADR 0027), with a note saying which setting it needs.
    /// </summary>
    internal ImmutableDictionary<string, SimSkillAside> Aside { get; }

    /// <summary>Whether no skill is used: none selected, only a shield, or only skills that step aside.</summary>
    internal bool IsEmpty => plans.IsEmpty;

    /// <summary>How a skill used only below an armor share steps aside in a fight without enemy fire (ADR 0030): its note starts so.</summary>
    internal const string ArmorWaitNote = "used once armor is below ";

    /// <summary>How a buff hive steps aside in a fight without talent triggers (ADR 0031): its charges turn on keys no rule would drive.</summary>
    internal const string ChargeNote = "its charges turn on buffs, which need talent triggers";

    /// <summary>How a skill whose use holds a buff steps aside in a fight without talent triggers (ADR 0032).</summary>
    internal const string HoldNote = "it turns on a buff while out, which needs talent triggers";

    /// <summary>How a hive charging skills steps aside with no other skill in the build whose uses it could reach (ADR 0031).</summary>
    internal const string ChargeSkillsNote = "no other skill of the build's runs a use its charges could reach";

    /// <summary>How a skill reviving its user steps aside in a fight without enemy fire (ADR 0037), where nothing downs them.</summary>
    internal const string ReviveNote = "it revives you once enemy fire downs you";

    /// <summary>An armor share as notes give it: "full" for 1, else a percentage of the maximum.</summary>
    internal static string ArmorShareText(double share) => share >= 1 ? "full" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{share * 100:G}% of its maximum");

    public static SkillDamageSchedule Create(GamePack pack, ImmutableDictionary<string, SkillPreview> previews, bool shieldsHandled = false,
        ImmutableDictionary<string, string>? context = null, double? distance = null, bool enemyFire = true, bool triggers = true)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(previews);
        context ??= ImmutableDictionary<string, string>.Empty;
        var plans = ImmutableArray.CreateBuilder<Plan>();
        var aside = ImmutableDictionary.CreateBuilder<string, SimSkillAside>(StringComparer.Ordinal);
        foreach (var slot in pack.ActiveSkillSlots)
        {
            if (!previews.TryGetValue(slot.ID, out var preview)) { continue; }
            var matches = pack.Simulation.Skills.Where(rule => rule.Skill == preview.SkillId).Take(2).ToArray();
            if (matches.Length == 0)
            {
                // A skill the pack says plays no part in a fight steps aside (ADR 0022).
                if (pack.Simulation.SkillsAside.FirstOrDefault(entry => entry.Skill == preview.SkillId) is { } entry) { aside[slot.ID] = entry; continue; }
                throw new SimulationException("skill-rule-required", $"No explicit simulation rule for {preview.SkillId}");
            }
            if (matches.Length > 1) { throw new SimulationException("skill-rule-duplicate", $"More than one simulation rule for {preview.SkillId}"); }
            var rule = matches[0];
            // A skill gated on the user's settings (ADR 0027) steps aside when they don't hold, before its outputs are read.
            if (!ResolveAssumptions.Matches(rule.RequiresContext, context))
            {
                aside[slot.ID] = new SimSkillAside { Skill = preview.SkillId, Why = GateNote(pack, rule.RequiresContext!) };
                continue;
            }
            // ADR 0030: without enemy fire the player's armor never falls, so a skill waiting for it is never used.
            if (rule.UseArmorBelow > 0 && !enemyFire)
            {
                aside[slot.ID] = new SimSkillAside { Skill = preview.SkillId, Why = ArmorWaitNote + ArmorShareText(rule.UseArmorBelow) };
                continue;
            }
            // ADR 0037: nor does anything down the player.
            if (rule.Revive is not null && !enemyFire)
            {
                aside[slot.ID] = new SimSkillAside { Skill = preview.SkillId, Why = ReviveNote };
                continue;
            }
            // ADR 0031: a buff hive's charges fire skill_charge for its own trigger rules, which only a fight driving them runs.
            if (rule.ChargeFor.Length > 0 && !triggers)
            {
                aside[slot.ID] = new SimSkillAside { Skill = preview.SkillId, Why = ChargeNote };
                continue;
            }
            // ADR 0032: likewise a skill whose use holds a buff (skill_out).
            if (!triggers && pack.Simulation.Triggers.Any(trigger => trigger.Talent == preview.SkillId && trigger.On == "skill_out"))
            {
                aside[slot.ID] = new SimSkillAside { Skill = preview.SkillId, Why = HoldNote };
                continue;
            }
            if (rule.Shield.Length > 0)
            {
                if (shieldsHandled) { continue; }
                throw new SimulationException("skill-shield-unsupported", "Shields require the incoming-damage scheduler");
            }
            double Read(string id)
            {
                if (id.Length == 0) { return 0; }
                var outputs = preview.Outputs.Where(output => output.Id == id).Take(2).ToArray();
                if (outputs.Length > 1) { throw new SimulationException("skill-output-invalid", $"Duplicate {preview.SkillId}.{id}"); }
                var output = outputs.FirstOrDefault();
                if (output is null || !double.IsFinite(output.Value) || output.Value < 0)
                { throw new SimulationException("skill-output-invalid", $"Missing, negative or non-finite {preview.SkillId}.{id}"); }
                return output.Value;
            }
            var window = Read(rule.Window);
            var cooldown = Read(rule.Cooldown);
            var rate = Read(rule.Rate);
            var impact = Read(rule.Impact);
            var impacts = 1d;
            var interval = Read(rule.ImpactInterval);
            if (rule.Impacts.Length > 0)
            {
                var count = Read(rule.Impacts);
                if (count < 1 || count != Math.Floor(count)) { throw new SimulationException("skill-impact-count-invalid", preview.SkillId); }
                if (rule.ImpactInterval.Length == 0) { impact *= count; }
                else if (interval > 0 || count == 1) { impacts = count; }
                else { throw new SimulationException("skill-output-invalid", $"Invalid impact interval for {preview.SkillId}"); }
            }
            var targets = rule.Targets.Length > 0 ? Read(rule.Targets) : 1;
            if (targets < 1 || targets != Math.Floor(targets) || targets > int.MaxValue) { throw new SimulationException("skill-output-invalid", $"Invalid target count for {preview.SkillId}"); }
            var stock = rule.Stock.Length > 0 ? Read(rule.Stock) : 1;
            if (stock < 1 || stock != Math.Floor(stock)) { throw new SimulationException("skill-output-invalid", $"Invalid stock for {preview.SkillId}"); }
            var statusRate = Read(rule.StatusRate);
            var statusWindow = Read(rule.StatusWindow);
            // Checked against its limit only where it can reach someone, so a fight it plays no part in isn't refused.
            var area = Read(rule.Area);
            var areaFull = Read(rule.AreaFull);
            // A use that runs from its end isn't over until its last impact.
            // ADR 0028: a use that lingers after its last impact (the Leech Hive's 2 s) ends that much later.
            var cycle = cooldown + (rule.CooldownFromEnd ? Math.Max(window, (impacts - 1) * interval + rule.EndDelay) : rule.CooldownAfter);
            // ADR 0029: the effects reach enemies beyond a distance later, by the fight's one distance where it has one.
            var delay = rule.FarFrom > 0 && distance is { } metres && metres > rule.FarFrom ? rule.FarImpactDelay : rule.ImpactDelay;
            var impactRepair = Read(rule.ImpactRepair);
            var killChance = Read(rule.KillChance);
            var repairCount = rule.RepairCount.Length > 0 ? Read(rule.RepairCount) : double.PositiveInfinity;
            if (repairCount < 1 || double.IsFinite(repairCount) && repairCount != Math.Floor(repairCount)) { throw new SimulationException("skill-output-invalid", $"Invalid repair count for {preview.SkillId}"); }
            // A cloud shorter than its first tick would restore nothing.
            var repairCloud = Read(rule.RepairCloud);
            if (rule.RepairCloud.Length > 0 && repairCloud < 0.5) { throw new SimulationException("skill-output-invalid", $"A repair cloud under 0.5 s for {preview.SkillId}"); }
            var chargeFor = Read(rule.ChargeFor);
            if (rule.ChargeFor.Length > 0 && !(chargeFor > 0)) { throw new SimulationException("skill-output-invalid", $"A charge lasting no time for {preview.SkillId}"); }
            var reviveSeconds = rule.Revive is { } reviving ? Read(reviving.Seconds) : 0;
            if (rule.Revive is not null && !(reviveSeconds > 0)) { throw new SimulationException("skill-output-invalid", $"A revive taking no time for {preview.SkillId}"); }
            if (killChance > 1) { throw new SimulationException("skill-output-invalid", $"An outright kill chance over 1 for {preview.SkillId}"); }
            if (!double.IsFinite(cycle) || cycle <= 0 || !double.IsFinite(impact) || !double.IsFinite(rate * window) || !double.IsFinite(statusRate * (window + statusWindow)))
            { throw new SimulationException("skill-cycle-invalid", $"Invalid cycle or damage for {preview.SkillId}"); }
            plans.Add(new(slot.ID, preview.SkillId, window, cycle, rate, impact, rule.RateArmorBonus, rule.RateRead == "live" && rule.Rate.Length > 0, StatusDamageState.Damaging.Contains(rule.Status) ? rule.Status : "",
                null, stock, rule.StockInterval, statusRate, statusWindow, rule.StatusArmorBonus, impacts, interval)
            {
                Cooldown = cooldown,
                Area = area,
                AreaFull = areaFull,
                AreaEdge = rule.AreaFull.Length > 0 ? rule.AreaEdge : 1,
                ControlStatus = rule.ControlStatus ? rule.Status : "",
                AreaFromPlayer = rule.AreaFromPlayer,
                Cloud = rule.IsCloud,
                Targets = (int)targets,
                ImpactRepair = impactRepair,
                RepairDelay = rule.RepairDelay,
                UseArmorBelow = rule.UseArmorBelow,
                Repair = Read(rule.Repair),
                LiveRepair = rule.RateRead == "live" && rule.Repair.Length > 0,
                RepairEvery = rule.RepairEvery,
                RepairLag = rule.RepairLag,
                RepairBelow = rule.RepairBelow > 0 ? rule.RepairBelow : 1,
                RepairCount = repairCount,
                RepairCloud = repairCloud,
                ChargeFor = chargeFor,
                ChargeSkills = rule.ChargeSkills,
                HolsteredEvery = rule.HolsteredEvery,
                HolsteredLag = rule.HolsteredLag,
                CooldownFromEnd = rule.CooldownFromEnd,
                Refresh = Read(rule.Refresh),
                DecoyHealth = Read(rule.DecoyHealth),
                MarksZones = rule.Zones is not null,
                ZoneHealth = rule.Zones is { } zones ? Read(zones.Count) * Read(zones.Health) : 0,
                ZoneFactor = rule.Zones is { Factor.Length: > 0 } scaled ? Read(scaled.Factor) : 1,
                Revive = rule.Revive,
                ReviveArmor = rule.Revive is { } revived ? Read(revived.Armor) : 0,
                ReviveSeconds = reviveSeconds,
                Family = pack.ActiveSkills.FirstOrDefault(skill => skill.ID == preview.SkillId)?.Family ?? "",
                EndDelay = rule.EndDelay,
                ArmorOnly = rule.ArmorOnly,
                Delay = delay,
                KillChance = killChance,
                KillProfiles = rule.KillProfiles,
                KillDelay = rule.KillDelay
            });
        }
        // ADR 0031: a hive charging skills reaches only another slot's running use; with none in the fight, it steps aside.
        var made = plans.ToImmutable();
        foreach (var charger in made.Where(plan => plan.ChargeSkills && !made.Any(other => other.Slot != plan.Slot && other.Reachable)))
        {
            aside[charger.Slot] = new SimSkillAside { Skill = charger.Skill, Why = ChargeSkillsNote };
            made = made.Remove(charger);
        }
        return new(made, aside.ToImmutable());
    }

    // Which settings a gated skill waits for, by their names: "needs Target has a weakpoint".
    private static string GateNote(GamePack pack, ImmutableDictionary<string, string> requires)
    {
        var names = requires.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var selector = pack.Selectors.FirstOrDefault(entry => entry.ID == pair.Key);
            var name = selector?.Name is { Length: > 0 } label ? label : pair.Key;
            return selector?.Values is { Length: 1 } ? name : $"{name}: {pair.Value}";
        });
        return "needs " + string.Join(", ", names) + " set";
    }

    /// <summary>This schedule with each slot's amounts against each target profile (P10).</summary>
    internal SkillDamageSchedule WithAmounts(IReadOnlyDictionary<int, SkillDamageSchedule> profiles) =>
        new([.. plans.Select(plan => plan with { Amounts = profiles.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Damage()[plan.Slot]) })], Aside);

    /// <summary>A slot's plan. A slot that loses its skill in some state has none, which only a broken pack can do.</summary>
    internal Plan For(string slot) => plans.FirstOrDefault(plan => plan.Slot == slot) ??
        throw new SimulationException("skill-output-invalid", $"Skill slot {slot} has no plan in this state");

    /// <summary>
    /// Whether the other schedule deploys the same skills at the same times, with damage of the same kinds: only the amounts may
    /// differ (a skill's damage against another target profile, P10).
    /// </summary>
    internal bool SameTiming(SkillDamageSchedule other) => plans.Length == other.plans.Length && plans.Zip(other.plans).All(pair =>
        pair.First.Slot == pair.Second.Slot && pair.First.Skill == pair.Second.Skill && pair.First.Window == pair.Second.Window &&
        pair.First.Cycle == pair.Second.Cycle && pair.First.Stock == pair.Second.Stock && pair.First.StockInterval == pair.Second.StockInterval && pair.First.StatusWindow == pair.Second.StatusWindow &&
        pair.First.Impacts == pair.Second.Impacts && pair.First.ImpactInterval == pair.Second.ImpactInterval && pair.First.Targets == pair.Second.Targets && pair.First.ImpactRepair == pair.Second.ImpactRepair && pair.First.Delay == pair.Second.Delay &&
        pair.First.RepairDelay == pair.Second.RepairDelay && pair.First.UseArmorBelow == pair.Second.UseArmorBelow &&
        pair.First.RepairEvery == pair.Second.RepairEvery && pair.First.RepairLag == pair.Second.RepairLag && pair.First.RepairBelow == pair.Second.RepairBelow &&
        pair.First.RepairCount == pair.Second.RepairCount && pair.First.RepairCloud == pair.Second.RepairCloud && pair.First.Repair == pair.Second.Repair &&
        pair.First.LiveRepair == pair.Second.LiveRepair && pair.First.DecoyHealth == pair.Second.DecoyHealth &&
        pair.First.MarksZones == pair.Second.MarksZones && pair.First.ZoneHealth == pair.Second.ZoneHealth && pair.First.ZoneFactor == pair.Second.ZoneFactor && pair.First.Revive == pair.Second.Revive && pair.First.ReviveArmor == pair.Second.ReviveArmor && pair.First.ReviveSeconds == pair.Second.ReviveSeconds && pair.First.ChargeFor == pair.Second.ChargeFor && pair.First.ChargeSkills == pair.Second.ChargeSkills && pair.First.Refresh == pair.Second.Refresh &&
        pair.First.CooldownFromEnd == pair.Second.CooldownFromEnd && pair.First.HolsteredEvery == pair.Second.HolsteredEvery && pair.First.HolsteredLag == pair.Second.HolsteredLag &&
        pair.First.ArmorOnly == pair.Second.ArmorOnly && pair.First.KillChance == pair.Second.KillChance && pair.First.KillDelay == pair.Second.KillDelay &&
        (pair.First.Rate > 0) == (pair.Second.Rate > 0) && (pair.First.Impact > 0) == (pair.Second.Impact > 0) && (pair.First.StatusRate > 0) == (pair.Second.StatusRate > 0));

    /// <summary>The stat adding to the armor factor of a slot's rate, or empty for none (R20).</summary>
    internal string RateArmorBonus(string slot) => rateArmorBonuses[slot];

    /// <summary>Each slot's impact and continuous rate, for dealing an event's damage as this schedule would (P10).</summary>
    internal ImmutableDictionary<string, (double Impact, double Rate, double StatusRate)> Damage() =>
        plans.ToImmutableDictionary(plan => plan.Slot, plan => (plan.Impact, plan.Rate, plan.StatusRate), StringComparer.Ordinal);

    /// <summary>Impacts plus 100 ms continuous-damage batches, including the final clipped window endpoint.</summary>
    internal ImmutableArray<SkillDamageEvent> DamageEvents(double duration, CancellationToken cancellationToken = default)
    {
        var events = ImmutableArray.CreateBuilder<SkillDamageEvent>();
        void Add(SkillDamageEvent entry)
        {
            if (events.Count == SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
            events.Add(entry with { HitGroup = events.Count + 1 });
        }
        foreach (var use in Uses(duration, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in UseEvents(use, cancellationToken)) { Add(entry); }
        }
        return [.. events.OrderBy(entry => entry.Time).ThenBy(entry => entry.Kind == SkillDamageKind.Impact)];
    }

    /// <summary>One use's impacts, then its rate's batches in order, then its status's.</summary>
    internal static IEnumerable<SkillDamageEvent> UseEvents(SkillUse use, CancellationToken cancellationToken)
    {
        // A control skill's status goes on as it's used (ADR 0017), ahead of anything else the use does at that moment.
        if (use.Control) { yield return new(use.Time, 0, use.Slot, SkillDamageKind.Control, use.Time); }
        for (var index = 0d; use.Impact > 0 && index < use.Impacts; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var time = use.Time + index * use.ImpactInterval;
            yield return new(time, use.Impact, use.Slot, SkillDamageKind.Impact, time);
            // ADR 0030: the impact's repair lands later, if the fight still runs then.
            var repairAt = time + use.RepairDelay;
            if (use.Repair > 0 && repairAt < use.FightEnd) { yield return new(repairAt, use.Repair, use.Slot, SkillDamageKind.Repair, repairAt); }
        }
        // ADR 0030: a repair skill checks the player's armor from the use through its window.
        for (var index = 0d; use.RepairEvery > 0 && use.Time + index * use.RepairEvery < use.WindowEnd; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(use.Time + index * use.RepairEvery, 0, use.Slot, SkillDamageKind.RepairCheck, use.Time);
        }
        foreach (var batch in Batches(use, use.WindowEnd, use.Rate, SkillDamageKind.ContinuousWindow, cancellationToken)) { yield return batch; }
        foreach (var batch in Batches(use, use.StatusEnd, use.StatusRate, SkillDamageKind.StatusDot, cancellationToken)) { yield return batch; }
    }

    /// <summary>
    /// What a running use adds when a charge lengthens it (ADR 0031): its checks on their grid, its rate's batches from the window's old end
    /// to its new, and its status's from the status's old end to its new.
    /// </summary>
    internal static IEnumerable<SkillDamageEvent> Lengthened(SkillUse use, double from, double to, double statusFrom, double statusTo, CancellationToken cancellationToken)
    {
        if (use.RepairEvery > 0)
        {
            // The first check the use's own didn't make: those stopped below its window's old end, by the same reckoning.
            var first = Math.Max(0, Math.Floor((from - use.Time) / use.RepairEvery) - 1);
            while (use.Time + first * use.RepairEvery < from) { first++; }
            for (var index = first; use.Time + index * use.RepairEvery < to; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(use.Time + index * use.RepairEvery, 0, use.Slot, SkillDamageKind.RepairCheck, use.Time);
            }
        }
        foreach (var batch in Batches(use with { Time = from }, to, use.Rate, SkillDamageKind.ContinuousWindow, cancellationToken)) { yield return batch; }
        foreach (var batch in Batches(use with { Time = statusFrom }, statusTo, use.StatusRate, SkillDamageKind.StatusDot, cancellationToken)) { yield return batch; }
    }

    private static IEnumerable<SkillDamageEvent> Batches(SkillUse use, double end, double rate, SkillDamageKind kind, CancellationToken cancellationToken)
    {
        if (rate == 0) { yield break; }
        var previous = use.Time;
        while (previous < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Split at integer boundaries too, keeping each batch inside a timeline bucket.
            var next = Math.Min(end, Math.Min(previous + 0.1, Math.Floor(previous) + 1));
            if (next <= previous) { throw new SimulationException("time-not-advancing", "Skill tick cannot advance time"); }
            yield return new(next, rate * (next - previous), use.Slot, kind, previous);
            previous = next;
        }
    }

    /// <summary>A run's fights on this schedule, with batches cut at these attempts, if any.</summary>
    public SkillFights Fights(double duration, ImmutableArray<double> boundaries, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 600) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        CheckBoundaries(boundaries, cancellationToken);
        return new(plans, duration, boundaries, cancellationToken);
    }

    /// <summary>
    /// A work limit a whole fight on this schedule would reach, from its cooldowns alone, so it doesn't wait for the rolls. A stream bounds its
    /// own as it goes, for a state that shortens them.
    /// </summary>
    internal void CheckWork(double duration, ImmutableArray<double> boundaries)
    {
        double deployments = 0, events = 0;
        foreach (var plan in plans)
        {
            var uses = Math.Ceiling(duration / plan.Cycle) + plan.Stock - 1;
            var window = (plan.Rate > 0 ? Math.Min(plan.Window, duration) : 0) + (plan.StatusRate > 0 ? Math.Min(Math.Max(plan.Window, (plan.Impacts - 1) * plan.ImpactInterval) + plan.StatusWindow, duration) : 0);
            deployments += uses;
            // A delayed repair is an event of its own beside each impact (ADR 0030).
            var impactEvents = (plan.Impact > 0 ? plan.Impacts > 1 ? Math.Min(plan.Impacts, Math.Ceiling(duration / plan.ImpactInterval)) : 1 : 0) * (plan.RepairDelay > 0 && plan.ImpactRepair > 0 ? 2 : 1);
            var checks = plan.RepairEvery > 0 ? Math.Ceiling(Math.Min(plan.Window, duration) / plan.RepairEvery) : 0;
            events += uses * (impactEvents + checks + Math.Ceiling(window * 10) + Math.Ceiling(window)) + boundaries.Length * Math.Ceiling(window / plan.Cycle);
            // A hive's pulses while holstered run on the fight's own grid (ADR 0031 stage 2c).
            if (plan.HolsteredEvery > 0) { events += Math.Ceiling(duration / plan.HolsteredEvery); }
        }
        if (deployments > SkillEventStream.UseLimit) { throw new SimulationException("skill-work-limit", "Too many skill deployments"); }
        if (events > SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
    }

    private static void CheckBoundaries(ImmutableArray<double> boundaries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = -1d;
        foreach (var boundary in boundaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(boundary) || boundary < 0 || boundary <= previous) { throw new ArgumentOutOfRangeException(nameof(boundaries)); }
            previous = boundary;
        }
    }

    /// <summary>Split continuous batches at enemy attempts; elapsed ticks precede same-time instantaneous events.</summary>
    internal static ImmutableArray<SkillDamageEvent> SplitAt(ImmutableArray<SkillDamageEvent> events, ImmutableArray<double> boundaries,
        CancellationToken cancellationToken = default)
    {
        CheckBoundaries(boundaries, cancellationToken);
        var result = ImmutableArray.CreateBuilder<SkillDamageEvent>();
        void Add(SkillDamageEvent entry)
        {
            if (result.Count == SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many split skill events"); }
            result.Add(entry);
        }
        foreach (var entry in events) { Cut(entry, boundaries, Add, cancellationToken); }
        return [.. result.OrderBy(entry => entry.Time).ThenBy(entry => entry.Kind == SkillDamageKind.Impact)];
    }

    /// <summary>One event's pieces, in order, for boundaries already checked.</summary>
    internal static void Cut(SkillDamageEvent entry, ImmutableArray<double> boundaries, Action<SkillDamageEvent> add, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind is not (SkillDamageKind.Impact or SkillDamageKind.Control or SkillDamageKind.Repair or SkillDamageKind.RepairCheck or SkillDamageKind.Charge or SkillDamageKind.Pulse) && !entry.Kind.IsBatch())
        { throw new SimulationException("skill-event-kind-unsupported", "Unknown skill damage kinds are not modeled"); }
        if (!double.IsFinite(entry.Start) || !double.IsFinite(entry.Time) || entry.Start < 0 || entry.Time < entry.Start ||
            entry.Kind.IsBatch() && entry.Time == entry.Start || !double.IsFinite(entry.Damage) || entry.Damage < 0)
        { throw new SimulationException("skill-event-invalid", "Skill events require finite times and nonnegative damage; continuous intervals must be positive"); }
        if (entry.Kind is SkillDamageKind.Impact or SkillDamageKind.Control or SkillDamageKind.Repair or SkillDamageKind.RepairCheck or SkillDamageKind.Charge or SkillDamageKind.Pulse) { add(entry); return; }
        var index = boundaries.BinarySearch(entry.Start);
        index = index < 0 ? ~index : index + 1;
        var start = entry.Start;
        var remaining = entry.Damage;
        while (index < boundaries.Length && boundaries[index] < entry.Time)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = boundaries[index++];
            var damage = Math.Min(remaining, entry.Damage * ((end - start) / (entry.Time - entry.Start)));
            add(entry with { Start = start, Time = end, Damage = damage });
            remaining -= damage;
            start = end;
        }
        add(entry with { Start = start, Damage = remaining });
    }

    /// <summary>Unlimited-dummy damage bins, integrating each active window exactly over bucket boundaries.</summary>
    internal ImmutableArray<double> DummyTimeline(double duration, CancellationToken cancellationToken = default)
    {
        var uses = Uses(duration, cancellationToken);
        var bins = new double[(int)Math.Ceiling(duration)];
        void Add(int bin, double damage)
        {
            var total = bins[bin] + damage;
            if (!double.IsFinite(total)) { throw new SimulationException("damage-overflow", "Skill damage overflowed"); }
            bins[bin] = total;
        }
        foreach (var use in uses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0d; index < use.Impacts; index++) { Add((int)(use.Time + index * use.ImpactInterval), use.Impact); }
            for (var bin = (int)use.Time; bin < bins.Length && bin < use.WindowEnd; bin++)
            {
                var overlap = Math.Max(0, Math.Min(bin + 1, use.WindowEnd) - Math.Max(bin, use.Time));
                Add(bin, overlap * use.Rate);
            }
        }
        return [.. bins];
    }

    /// <summary>
    /// Deploy at zero and each cooldown cycle before duration. Multiple impacts are an aggregate
    /// at deployment unless the rule spaces them. Continuous windows may overlap.
    /// </summary>
    internal ImmutableArray<SkillUse> Uses(double duration, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 600) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        cancellationToken.ThrowIfCancellationRequested();
        var uses = ImmutableArray.CreateBuilder<SkillUse>();
        foreach (var plan in plans)
        {
            // Only the stream keeps charges, so a stocked skill's uses can't be read off its cycle here.
            if (plan.Stock > 1) { throw new SimulationException("skill-output-invalid", $"Skill slot {plan.Slot} holds charges, which only fights schedule"); }
            for (var time = 0d; time < duration;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uses.Count == SkillEventStream.UseLimit) { throw new SimulationException("skill-work-limit", "Too many skill deployments"); }
                uses.Add(plan.UseAt(time, duration));
                time = plan.After(time);
            }
        }
        return [.. uses.OrderBy(use => use.Time)];
    }
}
