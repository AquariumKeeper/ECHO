using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>Compare Builds (F43), Rank Upgrades (F42) and the Simulate tab's form.</summary>
public sealed partial class EditorViewModel
{
    private RelayCommand<string>? rankUpgradesCommand;

    private SimulateViewModel? simulate;
    private bool simulationOwnerDisposed;
    /// <summary>Created on first use, after editor construction has completed.</summary>
    public SimulateViewModel Simulate
    {
        get
        {
            ObjectDisposedException.ThrowIf(simulationOwnerDisposed, this);
            return simulate ??= new(session, ui);
        }
    }

    /// <summary>The stat Rank Upgrades last ranked by, so the next dialog opens on it (Go's lastRankedStat).</summary>
    public string LastRankedStat { get; set; } = "";

    /// <summary>A Compare Builds dialog's model; the caller loads it (<see cref="CompareViewModel.LoadAsync"/>) and disposes it.</summary>
    public CompareViewModel CreateCompare() => new(this, ui);

    /// <summary>
    /// Opens Rank Upgrades for the item in the slot whose id is the parameter; it can run while the slot holds an item.
    /// The open dialog's model is <see cref="RankUpgrades"/>, which the window shows while it is set.
    /// </summary>
    public IRelayCommand<string> RankUpgradesCommand => rankUpgradesCommand ??= new(OpenRankUpgrades,
        slotId => slotId is { Length: > 0 } && session.Snapshot.State.Build.Equipment.GetValueOrDefault(slotId, "").Length > 0);

    /// <summary>The open Rank Upgrades dialog, or null. Apply and <see cref="CloseRankUpgrades"/> close it.</summary>
    public RankUpgradesViewModel? RankUpgrades { get; private set => SetProperty(ref field, value); }

    private void OpenRankUpgrades(string? slotId)
    {
        if (slotId is null || !RankUpgradesCommand.CanExecute(slotId)) { return; }
        CloseRankUpgrades();
        var dialog = new RankUpgradesViewModel(this, slotId, ui);
        RankUpgrades = dialog;
        dialog.Rank();
    }

    /// <summary>Opens Compare talents for a talent slot of the item in a slot (the inspector's Compare, a tile's talent badge).</summary>
    internal void CompareTalents(string slotId, string talentSlotId)
    {
        var item = Items.GetValueOrDefault(session.Snapshot.State.Build.Equipment.GetValueOrDefault(slotId, ""));
        if (Host is null || item?.TalentSlots.FirstOrDefault(slot => slot.ID == talentSlotId) is not { } talentSlot) { return; }
        Host.CompareTalents(new TalentCompareViewModel(this, slotId, talentSlot, ui));
    }

    /// <summary>Closes the Rank Upgrades dialog, if one is open.</summary>
    public void CloseRankUpgrades()
    {
        var open = RankUpgrades;
        RankUpgrades = null;
        open?.Dispose();
    }
}
