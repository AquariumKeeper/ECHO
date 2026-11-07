namespace CharacterPlanner.Desktop.ViewModels;

public sealed partial class EditorViewModel
{
    public ArtworkManagerViewModel? ArtworkManager { get; internal set => SetProperty(ref field, value); }
    internal event EventHandler? ArtworkChanged;

    /// <summary>Appearance-only refresh: no build operation, draft refresh, resolution or simulation invalidation.</summary>
    internal void SetArtwork(PackArtwork artwork)
    {
        Artwork = artwork;
        foreach (var slot in SlotGroups.SelectMany(group => group.Slots)) { slot.RefreshArtwork(); }
        foreach (var skill in SkillSlots) { skill.RefreshArtwork(); }
        Specialization.RefreshArtwork();
        ShowTiles(session.Snapshot.State.Build, resolved);
        Picker?.RefreshArtwork();
        if (Inspected is CandidateViewModel candidate && previewing is { } previewingItem && previewingItem.Picker.Highlighted is { } row &&
            previewBuild is not null && Items.TryGetValue(row.Id, out var item))
        { Inspected = candidate with { Art = Artwork.Item(item.ID, previewingItem.Slot.Id), Card = ItemCard(previewBuild, previewingItem.Slot.Id, item) }; }
        ArtworkChanged?.Invoke(this, EventArgs.Empty);
    }
}
