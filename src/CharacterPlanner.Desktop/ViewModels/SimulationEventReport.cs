using System.Collections.Immutable;
using System.Collections;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record SimulationEventRow(DummyEvent Event, string Slot)
{
    public string Time => Event.Time.ToString("G9");
    public string Kind => Event.Kind switch
    {
        DummyEventKind.Shot => "Shot",
        DummyEventKind.ReloadStarted => "Reload Started",
        DummyEventKind.ReloadCompleted => "Reload Completed",
        DummyEventKind.Kill => "Kill",
        DummyEventKind.TargetArrived => "Target Arrived",
        DummyEventKind.SwapStarted => "Swap Started",
        DummyEventKind.SwapCompleted => "Swap Completed",
        DummyEventKind.SkillImpact => "Skill Impact",
        DummyEventKind.SkillTick => "Skill Tick",
        DummyEventKind.EnemyShot => "Enemy Shot",
        DummyEventKind.EnemyHit => "Enemy Hit",
        DummyEventKind.ArmorBreak => "Armor Break",
        DummyEventKind.Downed => "Downed",
        DummyEventKind.EnemyShotBlocked => "Enemy Shot Blocked",
        DummyEventKind.ArmorRepaired => "Armor Repaired",
        DummyEventKind.HealthRepaired => "Health Repaired",
        DummyEventKind.ArmorKitStarted => "Armor Kit Started",
        DummyEventKind.ArmorKitUsed => "Armor Kit Used",
        DummyEventKind.ShieldRaised => "Shield Raised",
        DummyEventKind.ShieldHit => "Shield Hit",
        DummyEventKind.ShieldBreak => "Shield Break",
        DummyEventKind.ShieldRepaired => "Shield Repaired",
        DummyEventKind.TargetMarked => "Target Marked",
        DummyEventKind.PreservationStarted => "Preservation Started",
        DummyEventKind.BurnTick => "Burn Tick",
        DummyEventKind.BleedTick => "Bleed Tick",
        DummyEventKind.BonusArmorGranted => "Bonus Armor Granted",
        DummyEventKind.Pickup => "Pickup Collected",
        _ => Event.Kind.ToString()
    };
    public string Amount => Event.Kind.CarriesAmount() ? Event.Damage.ToString("G9") : "—";
}

/// <summary>First-fight events remain individual records in the engine's stable order, never averaged.</summary>
public sealed record SimulationEventReport(bool HasCapture, IReadOnlyList<SimulationEventRow> Rows, string Note)
{
    public static SimulationEventReport Empty { get; } = new(false, [], "");
    public string EmptyMessage => HasCapture && Rows.Count == 0 ? "No first-fight events were recorded." : "";

    public static SimulationEventReport From(DummyFightResult? result, bool captured, GamePack pack)
    {
        if (result is null || !captured) { return Empty; }
        var rows = new SimulationEventRows(result.FirstFightEvents.IsDefault ? [] : result.FirstFightEvents, pack);
        var note = $"First fight only: {rows.Count:N0} captured events, in recorded order. Times are seconds from fight start; values are individual events, not means across fights. Amount is outgoing damage, incoming damage, shield absorption or actual repair for the corresponding event. Preservation Started shows a scheduled repair budget, not delivered repair. — means not applicable; an unspecified source slot is also shown as —. Continuous repair entries are recorded at interval ends. This is not a complete state-change log.";
        if (result.TraceTruncated) { note += " Capture was truncated; further events were omitted. Run-wide summaries still cover the full run."; }
        return new(true, rows, note);
    }
}

/// <summary>IList lets Avalonia index a virtualized source without materializing the entire trace.</summary>
internal sealed class SimulationEventRows(ImmutableArray<DummyEvent> events, GamePack pack) : IReadOnlyList<SimulationEventRow>, IList
{
    private readonly Dictionary<string, string> slots = SlotNames(pack);
    public int Count => events.Length;
    public SimulationEventRow this[int index]
    {
        get
        {
            var entry = events[index];
            if (!slots.TryGetValue(entry.WeaponSlot, out var name))
            { slots[entry.WeaponSlot] = name = AssumptionViewModel.Humanize(entry.WeaponSlot); }
            return new(entry, name);
        }
    }
    private static Dictionary<string, string> SlotNames(GamePack pack)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = "—" };
        foreach (var slot in pack.Slots) { names[slot.ID] = Display(slot.ID, slot.Name); }
        foreach (var slot in pack.ActiveSkillSlots) { names.TryAdd(slot.ID, Display(slot.ID, slot.Name)); }
        return names;
        static string Display(string id, string name) => string.IsNullOrWhiteSpace(name) || name == id ? AssumptionViewModel.Humanize(id) : name;
    }
    public IEnumerator<SimulationEventRow> GetEnumerator()
    { for (var index = 0; index < Count; index++) { yield return this[index]; } }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;
    int IList.IndexOf(object? value)
    {
        if (value is not SimulationEventRow row) { return -1; }
        var index = events.IndexOf(row.Event);
        return index >= 0 && this[index] == row ? index : -1;
    }
    void ICollection.CopyTo(Array array, int index) { foreach (var row in this) { array.SetValue(row, index++); } }
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
