using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

public enum WeaponSwapMode { WhenEmpty, AfterKill }
public enum WeaponActionKind { Continue, Reload, Swap }
public sealed record WeaponAction(WeaponActionKind Kind, string From, string To, double StartedAt, double ReadyAt)
{
    public bool ReloadedDestination { get; init; }
    /// <summary>A swap made for a shield rather than by the rotation's mode; a refill never makes it obsolete.</summary>
    public bool Forced { get; init; }
    public double? SwapCompletedAt { get; init; }
    /// <summary>When a reload in this action begins, if it has one: in hand, or on arrival after a swap.</summary>
    internal double? ReloadStartsAt => Kind == WeaponActionKind.Reload ? StartedAt : ReloadedDestination ? SwapCompletedAt : null;
}

/// <summary>Static resolver snapshots and a magazine-preserving two-weapon clock for the fight loop.</summary>
public sealed class WeaponRotation
{
    private sealed record Weapon(string Slot, ResolvedBuild Resolution, double Magazine, double Interval, double Reload,
        double SwapSpeed, double SwitchMultiplier, bool NoReload);
    private readonly Weapon[] weapons;
    private readonly double[] ammo;
    // When each weapon's committed reload completes; until then its magazine reads what it held as the reload started, as a
    // single weapon's does: empty, unless it's a cover reload topping up a part-used magazine.
    private readonly double[] loadedAt = [0, 0];
    private readonly double[] reloadingRounds = [0, 0];
    private readonly WeaponSwapMode mode;
    private readonly double swapTime;
    private int active;

    private WeaponRotation(Weapon[] weapons, WeaponSwapMode mode, double swapTime)
    {
        this.weapons = weapons;
        this.mode = mode;
        this.swapTime = swapTime;
        ammo = [weapons[0].Magazine, weapons[1].Magazine];
    }

    public string ActiveSlot => weapons[active].Slot;
    public ResolvedBuild ActiveResolution => weapons[active].Resolution;
    public double ReadyAt { get; private set; }
    internal WeaponRotation Fresh() => new(weapons, mode, swapTime);

    /// <summary>A driven fight's reload time and swap speeds for a weapon, as it would be in hand at the current decision;
    /// without one each weapon keeps its own static timing.</summary>
    internal Func<string, (double Reload, double SwapSpeed, double SwitchMultiplier)>? DrivenTiming { get; set; }

    private (double Reload, double SwapSpeed, double SwitchMultiplier) Timing(Weapon weapon)
    {
        var timing = DrivenTiming?.Invoke(weapon.Slot) ?? (weapon.Reload, weapon.SwapSpeed, weapon.SwitchMultiplier);
        if (!double.IsFinite(timing.Reload) || timing.Reload < 0 || !double.IsFinite(timing.SwapSpeed) || !double.IsFinite(timing.SwitchMultiplier) || timing.SwitchMultiplier <= 0)
        { throw new SimulationException("weapon-stat-invalid", "Invalid rotation reload or swap speed"); }
        return timing;
    }
    internal ResolvedBuild Resolution(string slot) => weapons.Single(weapon => weapon.Slot == slot).Resolution;
    public double Rounds(string slot) => slot == weapons[0].Slot ? ammo[0] : slot == weapons[1].Slot ? ammo[1] : throw new ArgumentException("Unknown weapon slot", nameof(slot));

    /// <summary>The rounds a magazine reading sees at <paramref name="time"/>: those it held before a committed reload still under way.</summary>
    internal double ReadableRounds(string slot, double time)
    {
        var index = weapons[0].Slot == slot ? 0 : weapons[1].Slot == slot ? 1 : throw new ArgumentException("Unknown weapon slot", nameof(slot));
        return time < loadedAt[index] ? reloadingRounds[index] : ammo[index];
    }

    // The magazine is committed full now; readings see the rounds it held until the reload completes.
    private void CommitReload(int index, double readyAt, double rounds = 0)
    {
        ammo[index] = weapons[index].Magazine;
        loadedAt[index] = readyAt;
        reloadingRounds[index] = rounds;
    }

    public static WeaponRotation Create(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> assumptions, string firstSlot, string secondSlot,
        WeaponSwapMode mode = WeaponSwapMode.WhenEmpty, double swapTime = 1)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(assumptions);
        if (firstSlot == secondSlot || string.IsNullOrEmpty(firstSlot) || string.IsNullOrEmpty(secondSlot)) { throw new ArgumentException("Two different equipped weapon slots are required"); }
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (!double.IsFinite(swapTime) || swapTime <= 0 || swapTime > 10) { throw new ArgumentOutOfRangeException(nameof(swapTime)); }
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        Weapon Read(string slot)
        {
            var resolved = BuildResolver.Resolve(pack, build, assumptions.SetItem(GamePack.WeaponSlotSelector, slot));
            if (resolved.Diagnostics.Any(d => d.Severity == "error")) { throw new SimulationException("resolve-failed", "A rotation weapon failed resolution"); }
            if (!resolved.Loadout.Equipped.Any(item => item.WeaponSlot && item.SlotId == slot)) { throw new SimulationException("weapon-required", "Both rotation slots must hold weapons"); }
            var stats = resolved.Calculation.Stats;
            var timing = WeaponTiming.Read(stats);
            var speed = stats.GetValueOrDefault("effective_swap_speed", 0);
            var multiplier = stats.GetValueOrDefault("weapon_switch_speed_multiplier", 1);
            if (!double.IsFinite(speed) || !double.IsFinite(multiplier) || multiplier <= 0)
            { throw new SimulationException("weapon-stat-invalid", "Invalid rotation swap speed"); }
            return new(slot, resolved, timing.Magazine, timing.Interval, timing.Reload, speed, multiplier, timing.NoReload);
        }
        return new([Read(firstSlot), Read(secondSlot)], mode, swapTime);
    }

    // Half holster, half draw, each divided by its weapon's switch multiplier and one plus its swap speed (G427).
    private double SwapDuration(Weapon from, Weapon to)
    {
        var (_, fromSpeed, fromMultiplier) = Timing(from);
        var (_, toSpeed, toMultiplier) = Timing(to);
        return swapTime / 2 / fromMultiplier / Math.Max(0.1, 1 + fromSpeed) + swapTime / 2 / toMultiplier / Math.Max(0.1, 1 + toSpeed);
    }

    /// <summary>Handle a queued external kill once the current action is ready; never consumes a round. A swap the
    /// caller doesn't allow (a raised shield barring the other weapon) is skipped.</summary>
    public WeaponAction? SwapAfterExternalKill(double time, bool swapAllowed = true)
    {
        if (!double.IsFinite(time) || time < ReadyAt) { throw new ArgumentOutOfRangeException(nameof(time)); }
        var other = 1 - active;
        if (!swapAllowed || mode != WeaponSwapMode.AfterKill || !weapons[other].NoReload && ammo[other] < 1) { return null; }
        var from = weapons[active];
        var to = weapons[other];
        var next = time + SwapDuration(from, to);
        if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "External-kill swap cannot advance time"); }
        active = other;
        ReadyAt = next;
        return new(WeaponActionKind.Swap, from.Slot, to.Slot, time, next) { SwapCompletedAt = next };
    }

    /// <summary>Swap now, whatever the mode, once the current action is ready (a shield forcing its allowed weapon, or
    /// the build's weapon returning after a break). An empty destination reloads on arrival; no round is spent.</summary>
    internal WeaponAction ForceSwap(double time)
    {
        if (!double.IsFinite(time) || time < ReadyAt) { throw new ArgumentOutOfRangeException(nameof(time)); }
        var other = 1 - active;
        var from = weapons[active];
        var to = weapons[other];
        var swapped = time + SwapDuration(from, to);
        var next = swapped;
        var reloaded = !to.NoReload && ammo[other] < 1;
        if (reloaded) { next += Timing(to).Reload; }
        if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "Forced swap cannot advance time"); }
        if (reloaded) { CommitReload(other, next); }
        active = other;
        ReadyAt = next;
        return new(WeaponActionKind.Swap, from.Slot, to.Slot, time, next) { ReloadedDestination = reloaded, SwapCompletedAt = swapped, Forced = true };
    }

    /// <summary>Entering cover (ADR 0009) reloads the weapon in hand when its magazine isn't full; holstered weapons never
    /// reload themselves. Swapping when empty, a magazine stays as it is, so the weapon empties and the swap comes, unless the
    /// caller doesn't allow the swap (a raised shield barring the other weapon). Null when there's nothing to reload.</summary>
    internal WeaponAction? CoverReload(double time, bool swapAllowed = true)
    {
        if (!double.IsFinite(time) || time < ReadyAt) { throw new ArgumentOutOfRangeException(nameof(time)); }
        var weapon = weapons[active];
        if (weapon.NoReload || ammo[active] >= weapon.Magazine || mode == WeaponSwapMode.WhenEmpty && swapAllowed) { return null; }
        var reload = Timing(weapon).Reload;
        var next = time + reload;
        if (!double.IsFinite(next) || reload > 0 && next <= time) { throw new SimulationException("time-not-advancing", "Cover reload cannot advance time"); }
        CommitReload(active, next, ammo[active]);
        ReadyAt = next;
        return new(WeaponActionKind.Reload, weapon.Slot, weapon.Slot, time, next);
    }

    private void UndoReload(int index)
    {
        ammo[index] = reloadingRounds[index];
        loadedAt[index] = 0;
        reloadingRounds[index] = 0;
    }

    /// <summary>
    /// A refill before a committed action starts makes it obsolete: an empty swap or a reload isn't needed any more, and neither is a
    /// reload waiting at the end of a swap under way. The clock goes back to when the action would have begun.
    /// </summary>
    /// <param name="action">The queued action.</param>
    /// <param name="swapBegun">Whether the fight has begun this swap, so what's left of it is the reload on arrival.</param>
    /// <returns>When the weapon in hand can act again.</returns>
    internal double Cancel(WeaponAction action, bool swapBegun)
    {
        var from = weapons[0].Slot == action.From ? 0 : 1;
        var to = weapons[0].Slot == action.To ? 0 : 1;
        if (action.Kind == WeaponActionKind.Reload)
        {
            UndoReload(from);
            return ReadyAt = action.StartedAt;
        }
        if (action.ReloadedDestination) { UndoReload(to); }
        if (swapBegun) { return ReadyAt = action.SwapCompletedAt!.Value; }
        active = from;
        return ReadyAt = action.StartedAt;
    }

    /// <summary>A triggered refill (Dread Edict, Kill Confirmed) fills the weapon in hand, which may not be the
    /// weapon a scheduled swap is heading to.</summary>
    internal void Refill(string slot)
    {
        var index = weapons[0].Slot == slot ? 0 : weapons[1].Slot == slot ? 1 : throw new ArgumentException("Unknown weapon slot", nameof(slot));
        if (!weapons[index].NoReload) { ammo[index] = weapons[index].Magazine; }
    }

    /// <summary>Commit one shot at or after ReadyAt. Holstered weapons retain ammo and never reload themselves.
    /// A shot whose triggers refill the magazine leaves it full instead of spending a round. A driven fight passes
    /// the interval in effect for this shot; reload and swap times stay the weapons' own. With <paramref name="stopAt"/> the
    /// interval ends there if that comes first (a cover boundary), so the next decision is taken then.</summary>
    public WeaponAction Fire(double time, bool killed = false, bool refilled = false, double? interval = null, bool swapAllowed = true, double? stopAt = null)
    {
        if (!double.IsFinite(time) || time < ReadyAt) { throw new ArgumentOutOfRangeException(nameof(time)); }
        var from = weapons[active];
        var other = 1 - active;
        var remaining = from.NoReload ? ammo[active] : refilled ? from.Magazine : ammo[active] - 1;
        if (interval is { } given && (!double.IsFinite(given) || given <= 0)) { throw new ArgumentOutOfRangeException(nameof(interval)); }
        var next = time + (interval ?? from.Interval);
        if (stopAt < next) { next = stopAt.Value; }
        var kind = WeaponActionKind.Continue;
        var destination = active;
        var started = next;
        var reloadedDestination = false;
        // WhenEmpty always alternates, reloading an empty destination after the swap, so both
        // weapons keep cycling. AfterKill only swaps to a weapon that can fire immediately.
        // A swap the caller doesn't allow falls back to reloading (WhenEmpty) or continuing (AfterKill).
        if (swapAllowed && (mode == WeaponSwapMode.WhenEmpty && remaining < 1
            || mode == WeaponSwapMode.AfterKill && killed && (weapons[other].NoReload || ammo[other] >= 1)))
        {
            kind = WeaponActionKind.Swap;
            destination = other;
            next += SwapDuration(from, weapons[other]);
        }
        else if (!from.NoReload && remaining < 1)
        {
            kind = WeaponActionKind.Reload;
            next += Timing(from).Reload;
            remaining = from.Magazine;
        }
        double? swappedAt = kind == WeaponActionKind.Swap ? next : null;
        if (destination != active && !weapons[destination].NoReload && ammo[destination] < 1)
        {
            next += Timing(weapons[destination]).Reload;
            reloadedDestination = true;
        }
        if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "Weapon action cannot advance time"); }
        ammo[active] = remaining;
        if (kind == WeaponActionKind.Reload) { CommitReload(active, next); }
        if (reloadedDestination) { CommitReload(destination, next); }
        active = destination;
        ReadyAt = next;
        return new(kind, from.Slot, weapons[active].Slot, started, next) { ReloadedDestination = reloadedDestination, SwapCompletedAt = swappedAt };
    }
}
