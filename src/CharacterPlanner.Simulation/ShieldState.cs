using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

public sealed record ShieldHit(double Absorbed, bool Broken);
public sealed record ShieldRecovery(double Repaired, double? RaisedAt);

/// <summary>One always-raised shield with static Core capacity, active regeneration and break cooldown.</summary>
public sealed class ShieldState
{
    private readonly double capacity;
    private readonly double regeneration;
    private readonly double cooldown;
    private double clock;
    private double readyAt;
    private double brokenAt = double.NaN;
    public double Health { get; private set; }
    public bool IsUp => Health > 0;
    public bool Full => Health == capacity;

    public ShieldState(double capacity, double regeneration, double cooldown)
    {
        if (!double.IsFinite(capacity) || capacity <= 0 || !double.IsFinite(regeneration) || regeneration < 0 ||
            !double.IsFinite(cooldown) || cooldown <= 0)
        { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        this.capacity = capacity;
        this.regeneration = regeneration;
        this.cooldown = cooldown;
        Health = capacity;
    }

    public ShieldRecovery Advance(double time, double queuedRate = 0)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time)); }
        if (!double.IsFinite(queuedRate) || queuedRate < 0) { throw new ArgumentOutOfRangeException(nameof(queuedRate)); }
        var rate = regeneration + queuedRate;
        if (!double.IsFinite(rate)) { throw new SimulationException("repair-overflow", "Shield repair rate overflowed"); }
        double repaired = 0;
        double? raised = null;
        if (!IsUp && readyAt <= time) { Health = capacity; raised = readyAt; }
        else if (IsUp)
        {
            var missing = capacity - Health;
            var elapsed = time - clock;
            repaired = rate == 0 || elapsed == 0 ? 0 : elapsed >= missing / rate ? missing : rate * elapsed;
            Health = repaired == missing ? capacity : Math.Min(capacity, Health + repaired);
        }
        clock = time;
        return new(repaired, raised);
    }

    /// <summary>Repair a raised shield, capped at its capacity; a broken shield receives none. Advance the shield first.</summary>
    public double Repair(double amount, double time)
    {
        if (!double.IsFinite(time) || time != clock) { throw new ArgumentOutOfRangeException(nameof(time), "Advance the shield before repairing it"); }
        if (!double.IsFinite(amount) || amount < 0) { throw new ArgumentOutOfRangeException(nameof(amount)); }
        if (!IsUp) { return 0; }
        var repaired = Math.Min(amount, capacity - Health);
        Health = repaired == capacity - Health ? capacity : Health + repaired;
        return repaired;
    }

    public double Capacity => capacity;

    /// <summary>Whether a broken shield's cooldown is running now; one that broke at this very moment hasn't started yet.</summary>
    public bool CoolingDown(double time) => !IsUp && brokenAt < time && readyAt > time;

    /// <summary>When the shield last broke, NaN before its first break.</summary>
    public double BrokenAt => brokenAt;

    /// <summary>
    /// Takes time off a broken shield's cooldown: <paramref name="share"/> of the cooldown plus <paramref name="seconds"/>, ending
    /// no sooner than now. Advance the shield first, and again after, to raise it if it's ready. A raised shield is untouched, and
    /// so is one that broke at this very moment, so a rule on its break can't keep it up.
    /// </summary>
    public void Shorten(double share, double seconds, double time)
    {
        if (!double.IsFinite(time) || time != clock) { throw new ArgumentOutOfRangeException(nameof(time), "Advance the shield before shortening its cooldown"); }
        if (!double.IsFinite(share) || share < 0 || !double.IsFinite(seconds) || seconds < 0) { throw new ArgumentOutOfRangeException(nameof(share)); }
        if (IsUp || brokenAt == time) { return; }
        readyAt = Math.Max(time, readyAt - (share * cooldown + seconds));
    }

    /// <summary>A raised shield absorbs a whole attack; excess raw damage is discarded even when it breaks.</summary>
    public ShieldHit Apply(double rawDamage, double time)
    {
        if (!double.IsFinite(time) || time != clock) { throw new ArgumentOutOfRangeException(nameof(time), "Advance the shield before applying damage"); }
        if (!double.IsFinite(rawDamage) || rawDamage < 0) { throw new ArgumentOutOfRangeException(nameof(rawDamage)); }
        var absorbed = Math.Min(Health, rawDamage);
        var broken = IsUp && absorbed == Health;
        var ready = time + cooldown;
        if (broken && (!double.IsFinite(ready) || ready <= time))
        { throw new SimulationException("time-not-advancing", "Shield cooldown cannot advance time"); }
        Health -= absorbed;
        if (broken) { (readyAt, brokenAt) = (ready, time); }
        return new(absorbed, broken);
    }
}

internal sealed record ShieldProfile(string Slot, string Skill, double Health, double Regeneration, double Cooldown)
{
    internal ShieldState CreateState() => new(Health, Regeneration, Cooldown);

    private static (string Slot, SkillPreview Preview, SimSkillDefinition Rule) Selected(GamePack pack, ResolvedBuild resolved)
    {
        var candidates = resolved.ActiveSkillPreviews.SelectMany(pair => pack.Simulation.Skills
            .Where(rule => rule.Skill == pair.Value.SkillId && rule.Shield.Length > 0)
            .Select(rule => (Slot: pair.Key, Preview: pair.Value, Rule: rule))).ToArray();
        if (candidates.Length != 1) { throw new SimulationException("shield-required", "Exactly one selected shield with an explicit simulation rule is required"); }
        return candidates[0];
    }

    /// <summary>Whether the selected shield may be held with the weapon in <paramref name="slot"/> (its slot list and excluded weapon types).</summary>
    internal static bool Allows(GamePack pack, ResolvedBuild resolved, string slot)
    {
        var rule = Selected(pack, resolved).Rule;
        var weapon = resolved.Loadout.Equipped.FirstOrDefault(item => item.WeaponSlot && item.SlotId == slot);
        return weapon is not null && (rule.Weapons.Length == 0 || rule.Weapons.Contains(slot)) && !weapon.Item.Tags.Any(rule.ExcludeTags.Contains);
    }

    /// <summary>The selected shield's profile; <paramref name="requireAllowed"/> also requires it to allow the active weapon
    /// (a rotation checks its two weapons itself).</summary>
    internal static ShieldProfile Read(GamePack pack, ResolvedBuild resolved, string active, bool requireAllowed = true)
    {
        var (slot, preview, rule) = Selected(pack, resolved);
        if (rule.Rate.Length > 0 || rule.Window.Length > 0 || rule.Impact.Length > 0 || rule.Impacts.Length > 0 || rule.Status.Length > 0 || rule.CooldownFromEnd)
        { throw new SimulationException("shield-rule-unsupported", "Shield rules cannot also define damage, status or active windows"); }
        if (requireAllowed && !Allows(pack, resolved, active))
        { throw new SimulationException("shield-weapon-unsupported", "The active weapon is not allowed by this shield; add a weapon rotation with an allowed weapon to model shield swaps"); }
        double Read(string id)
        {
            var outputs = preview.Outputs.Where(output => output.Id == id).ToArray();
            if (id.Length == 0 || outputs.Length != 1 || !double.IsFinite(outputs[0].Value) || outputs[0].Value < 0)
            { throw new SimulationException("shield-output-invalid", $"Missing or invalid {preview.SkillId}.{id}"); }
            return outputs[0].Value;
        }
        var health = Read(rule.Shield);
        var regeneration = rule.Regeneration.Length == 0 ? 0 : Read(rule.Regeneration);
        var cooldown = Read(rule.Cooldown);
        if (health <= 0 || cooldown <= 0) { throw new SimulationException("shield-output-invalid", "Shield health and cooldown must be positive"); }
        return new(slot, preview.SkillId, health, regeneration, cooldown);
    }
}
