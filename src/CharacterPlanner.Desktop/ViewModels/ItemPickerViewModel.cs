using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Core.Engine;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A stat a picker row compares: its value with the item equipped, and whether that is up or down on the build as it is.</summary>
public sealed record CompareValue(string Label, string Value, int Direction)
{
    public bool IsUp => Direction > 0;
    public bool IsDown => Direction < 0;
    public string Text => Direction > 0 ? $"{Value} ▲" : Direction < 0 ? $"{Value} ▼" : Value;
}

/// <summary>
/// One item in the picker: its rarity's colour, a caption (rarity and fixed talent), where it is equipped, its compared
/// stats and, when sorting by a stat, how equipping it changes that stat.
/// </summary>
public sealed class ItemRowViewModel(string id, string name, string accent, string caption, string marker, ImmutableArray<CompareValue> stats, string change, ItemArt art,
    ImmutableArray<string> dots) : ViewModelBase
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Accent { get; } = accent;
    public string Caption { get; } = caption;
    public string Marker { get; } = marker;
    public ImmutableArray<CompareValue> Stats { get; } = stats;
    public string Change { get; } = change;
    public ImmutableArray<string> Dots { get; } = dots;
    public ItemArt Art { get; internal set => SetProperty(ref field, value); } = art;
    public bool HasMarker => Marker.Length > 0;
    public bool HasChange => Change.Length > 0;
}

/// <summary>
/// The item picker for one slot (F40), as the Go app's: search, rarity and family filters, sorting by name or by how
/// much equipping each item changes a stat, and rows comparing each item's key stats with the build as it is. The
/// comparison resolves the build with every item of the slot in the background, again after each edit. Highlighting
/// a row previews it in the headline, summary and inspector, whose Equip applies it.
/// </summary>
public sealed class ItemPickerViewModel : ViewModelBase, IDisposable
{
    public string Coverage => comparison is null ? EffectCoverage.ComparisonNote : EffectCoverage.ComparisonDisclosure(
        comparison.CandidateCoverage.Values.Prepend(comparison.Coverage));
    private readonly EditorViewModel editor;
    private readonly SlotViewModel slot;
    private readonly LatestRevisionJob<ItemComparison> comparer;
    private SessionSnapshot? snapshot;
    private ItemComparison? comparison;
    private string search = "";
    private Choice rarity;
    private Choice family;
    private Choice sortBy = ByName;
    private ImmutableArray<ItemRowViewModel> rows = [];
    private ItemRowViewModel? highlighted;

    internal ItemPickerViewModel(EditorViewModel editor, SlotViewModel slot, ImmutableArray<StatDefinition> sortStats, IUiDispatcher ui)
    {
        (this.editor, this.slot) = (editor, slot);
        Title = $"Select {slot.Name}";
        // The slot's rarities, and its families: brands and gear sets for gear, categories for weapons.
        Rarities = [AnyRarity, .. editor.Rarities.Where(entry => slot.Entries.Any(item => item.Rarity?.ID == entry.Id))];
        rarity = Rarities[0];
        var weapons = slot.Entries.Any(entry => entry.Set is null && entry.Family is not null);
        FamilyCaption = weapons ? "Weapon category" : "Brand or gear set";
        Families = [new("", weapons ? "All categories" : "All brands and sets"), .. slot.Entries.Select(entry => entry.Family).OfType<Choice>().Distinct()
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];
        family = Families[0];
        SortOptions = [ByName, .. sortStats.Select(stat => new Choice(stat.ID, stat.Name))];
        comparer = new(result => ui.Post(() => ShowComparison(result)));
        EquipCommand = new RelayCommand(Equip, () => Highlighted is not null);
        CloseCommand = new RelayCommand(() => editor.Browse(null));
    }

    /// <summary>Sorting by name; the equipped item comes first.</summary>
    public static readonly Choice ByName = new("", "Name");

    public string Title { get; }
    public string SlotId => slot.Id;
    public ImmutableArray<Choice> Rarities { get; }
    public bool HasRarities => Rarities.Length > 1;
    public ImmutableArray<Choice> Families { get; }
    public string FamilyCaption { get; }

    /// <summary>The rarity filter's "everything" option.</summary>
    public static readonly Choice AnyRarity = new("", "All rarities");
    public bool HasFamilies => Families.Length > 1;
    /// <summary>Name, then each compared and summary stat, by how much equipping the item raises it.</summary>
    public ImmutableArray<Choice> SortOptions { get; }

    public string Search { get => search; set { if (SetProperty(ref search, value ?? "")) { Rebuild(); } } }
    public Choice Rarity { get => rarity; set { if (value is not null && SetProperty(ref rarity, value)) { Rebuild(); } } }
    public Choice Family { get => family; set { if (value is not null && SetProperty(ref family, value)) { Rebuild(); } } }
    public Choice SortBy { get => sortBy; set { if (value is not null && SetProperty(ref sortBy, value)) { Rebuild(); } } }

    public ImmutableArray<ItemRowViewModel> Rows { get => rows; private set => SetProperty(ref rows, value); }

    /// <summary>The highlighted row, previewed in the headline, summary and inspector, and which Equip applies.</summary>
    public ItemRowViewModel? Highlighted
    {
        get => highlighted;
        set
        {
            if (!SetProperty(ref highlighted, value)) { return; }
            EquipCommand.NotifyCanExecuteChanged();
            // The highlighted item previews in the headline, summary and inspector, as Go's picker did.
            editor.Preview(slot, value, this);
        }
    }


    /// <summary>The items are being compared with the build as it is; their stats show once they are.</summary>
    public bool IsComparing { get; private set => SetProperty(ref field, value); }

    /// <summary>The latest comparison, for callers (and tests) that need to wait for it.</summary>
    public Task Comparison { get; private set; } = Task.CompletedTask;

    public IRelayCommand EquipCommand { get; }
    public IRelayCommand CloseCommand { get; }

    /// <summary>Shows a state: markers at once, and the items compared with it once that's done.</summary>
    internal void RefreshArtwork()
    {
        foreach (var row in Rows) { row.Art = editor.Artwork.Item(row.Id, slot.Id); }
    }

    internal void Show(SessionSnapshot shown)
    {
        snapshot = shown;
        comparison = null;
        OnPropertyChanged(nameof(Coverage));
        IsComparing = true;
        var ids = slot.Entries.Select(entry => entry.Item.ID).ToImmutableArray();
        var id = slot.Id;
        var session = editor.Session;
        Comparison = comparer.Start(shown, (state, token) => session.CompareItems(state, id, ids, token));
        Rebuild();
    }

    private void ShowComparison(JobResult<ItemComparison> result)
    {
        if (!comparer.IsCurrent(result)) { return; }
        IsComparing = false;
        // Never leave another revision's numbers on show.
        comparison = result.Value;
        OnPropertyChanged(nameof(Coverage));
        if (comparison?.Problem is { } problem) { editor.Message = problem; }
        if (result.Error is { } error) { editor.Message = $"The items couldn't be compared: {error.Message}"; }
        Rebuild();
    }

    private void Rebuild()
    {
        if (snapshot is null) { return; }
        var build = snapshot.State.Build;
        var equipped = build.Equipment.GetValueOrDefault(slot.Id, "");
        var current = comparison?.Current?.Calculation.Stats;
        var sortStat = editor.Statistics.GetValueOrDefault(sortBy.Id);
        var shown = slot.Entries.Where(entry => entry.Matches(search, rarity.Id, family.Id))
            .Select(entry => (Entry: entry, Delta: Delta(entry.Item.ID, sortStat, current)))
            .ToList();
        // By name, the equipped item first; by a stat, the biggest gain first, then by name, and items without a
        // comparison yet last.
        IEnumerable<(ItemEntry Entry, double? Delta)> ordered = sortStat is null
            ? shown.OrderBy(row => row.Entry.Item.ID == equipped ? 0 : 1)
            : shown.OrderBy(row => row.Delta is null ? 1 : 0).ThenByDescending(row => row.Delta ?? 0);
        Rows = [.. ordered.Select(row => Row(row.Entry, build.Equipment, equipped, current, sortStat, row.Delta))];
        var keep = highlighted?.Id;
        Highlighted = Rows.FirstOrDefault(row => row.Id == keep);
    }

    private double? Delta(string itemId, StatDefinition? stat, ImmutableDictionary<string, double>? current) =>
        stat is not null && current is not null && comparison?.Candidates.GetValueOrDefault(itemId) is { } candidate
            ? candidate.Calculation.Stats.GetValueOrDefault(stat.ID) - current.GetValueOrDefault(stat.ID)
            : null;

    private ItemRowViewModel Row(ItemEntry entry, ImmutableDictionary<string, string> equipment, string equipped,
        ImmutableDictionary<string, double>? current, StatDefinition? sortStat, double? delta)
    {
        var item = entry.Item;
        var talent = item.TalentSlots.Where(talentSlot => talentSlot.Choices.Length == 1)
            .Select(talentSlot => editor.Talents.GetValueOrDefault(talentSlot.Choices[0])).OfType<TalentDefinition>().FirstOrDefault();
        var caption = string.Join(" · ", new[] { entry.Rarity?.Name, talent?.Name }.OfType<string>().Where(text => text.Length > 0));
        var candidate = comparison?.Candidates.GetValueOrDefault(item.ID)?.Calculation.Stats;
        var stats = slot.CompareStats.Select(stat => candidate is null || current is null
            ? new CompareValue(Label(stat), "–", 0)
            : new CompareValue(Label(stat), Formatting.Value(stat.Format, candidate.GetValueOrDefault(stat.ID)),
                Formatting.Direction(candidate.GetValueOrDefault(stat.ID), current.GetValueOrDefault(stat.ID))));
        var change = sortStat is not null && delta is { } value ? Formatting.Delta(sortStat.Format, value) : "";
        return new(item.ID, item.Name, entry.Rarity?.Color is { Length: > 0 } color ? color : "Transparent", caption,
            Marker(item, equipment, equipped), [.. stats], change, editor.Artwork.Item(item.ID, slot.Id), editor.AttributeDots(snapshot!.State.Build, slot.Id, item));
    }

    /// <summary>"Equipped" for the slot's item, or "In (slot)" when an item of the same name is in another slot (the game lists a weapon once per weapon slot).</summary>
    private string Marker(ItemDefinition item, ImmutableDictionary<string, string> equipment, string equipped)
    {
        if (item.ID == equipped) { return "Equipped"; }
        var other = editor.Pack.Slots.FirstOrDefault(other => other.ID != slot.Id
            && editor.Items.GetValueOrDefault(equipment.GetValueOrDefault(other.ID, ""))?.Name == item.Name);
        return other is null ? "" : $"In {other.Name}";
    }

    private void Equip()
    {
        if (Highlighted is { } row && slot.Equip(row.Id)) { editor.Browse(null); }
    }

    private static string Label(StatDefinition stat) => stat.ShortName.Length > 0 ? stat.ShortName : stat.Name;

    public void Dispose() => comparer.Dispose();
}
