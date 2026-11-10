using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A titled group of loadout tiles (Agent, Weapons, Gear, Skills), with the most tiles a row of it holds (0 for the grid's).</summary>
public sealed record TileGroupViewModel(string Name, int Columns, ImmutableArray<TileViewModel> Tiles);

/// <summary>
/// One loadout tile, as the Go app's slot tiles: the slot's name, the item or choice in it (muted when empty), a
/// subtitle, the rarity's accent, dots in the colours of its configured attributes, its art, and its talent. Selecting
/// a tile shows what it holds in the inspector; the inspector does the editing.
/// </summary>
public sealed class TileViewModel : ViewModelBase
{
    private string title = "Empty";
    private string subtitle = "";
    private bool muted = true;
    private string accent = "";
    private ImmutableArray<string> dots = [];
    private ItemArt art = ItemArt.None;
    private string talentName = "";
    private string? talentIcon;

    internal TileViewModel(string caption, object target, Action<TileViewModel> select)
    {
        Caption = caption;
        Target = target;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>The slot's name.</summary>
    public string Caption { get; }
    /// <summary>What the inspector shows for this tile: a slot, a skill slot, the specialization or the SHD Watch.</summary>
    public object Target { get; }
    public IRelayCommand SelectCommand { get; }

    public string Title { get => title; private set { if (SetProperty(ref title, value)) { OnPropertyChanged(nameof(AccessibleName)); } } }
    public string Subtitle { get => subtitle; private set { if (SetProperty(ref subtitle, value)) { OnPropertyChanged(nameof(HasSubtitle)); } } }
    public bool HasSubtitle => Subtitle.Length > 0;
    /// <summary>Nothing is chosen ("Empty", "None"); the title reads muted.</summary>
    public bool IsMuted { get => muted; private set => SetProperty(ref muted, value); }
    /// <summary>The rarity's colour for the accent bar and tint, or "" for none.</summary>
    public string Accent { get => accent; private set { if (SetProperty(ref accent, value)) { OnPropertyChanged(nameof(HasAccent)); } } }
    public bool HasAccent => Accent.Length > 0;
    /// <summary>The item is Prototype: its accent is Prototype purple and the card carries circuit traces.</summary>
    public bool IsPrototype { get; private set => SetProperty(ref field, value); }
    /// <summary>What seeds the tile's circuit traces, so each card's pattern is its own and stays put.</summary>
    public string Seed => Caption;
    /// <summary>The colours of the item's configured attributes' categories, in attribute-slot order.</summary>
    public ImmutableArray<string> Dots { get => dots; private set => SetProperty(ref dots, value); }
    public ItemArt Art { get => art; private set => SetProperty(ref art, value); }
    public string TalentName { get => talentName; private set { if (SetProperty(ref talentName, value)) { OnPropertyChanged(nameof(HasTalent)); } } }
    public string? TalentIcon { get => talentIcon; private set => SetProperty(ref talentIcon, value); }
    public bool HasTalent => TalentName.Length > 0;
    /// <summary>The talent badge's Compare talents, for the tile's first chosen talent (as Go's badge), or null.</summary>
    public IRelayCommand? TalentCommand { get; private set; }
    public bool IsSelected { get; internal set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(SelectionStatus)); } } }

    /// <summary>What assistive technology reads for the tile: its slot and what it holds ("Mask: 5.11 Tactical Mask").</summary>
    public string AccessibleName => $"{Caption}: {Title}";
    /// <summary>"Selected" for the tile the inspector shows, read by assistive technology.</summary>
    public string SelectionStatus => IsSelected ? "Selected" : "";

    internal void Show(string title, string subtitle = "", bool muted = false, string accent = "", ImmutableArray<string> dots = default,
        ItemArt? art = null, string talentName = "", string? talentIcon = null, IRelayCommand? talentCommand = null, bool prototype = false)
    {
        IsPrototype = prototype;
        TalentCommand = talentCommand;
        OnPropertyChanged(nameof(TalentCommand));
        (Title, Subtitle, IsMuted, Accent) = (title, subtitle, muted, accent);
        Dots = dots.IsDefault ? [] : dots;
        Art = art ?? ItemArt.None;
        (TalentName, TalentIcon) = (talentName, talentIcon);
    }
}

/// <summary>The SHD Watch's ranks, as the inspector shows them for the Account upgrades tile.</summary>
public sealed record AccountUpgradesViewModel(ImmutableArray<RankViewModel> Upgrades);

/// <summary>An item as the inspector heads it: its name, caption, rarity colour and art, core stats and brand bonuses.</summary>
public interface IItemSummary
{
    string Title { get; }
    string HeaderCaption { get; }
    string Accent { get; }
    bool HasAccent { get; }
    /// <summary>Whether the item is Prototype, for the header's circuit traces.</summary>
    bool IsPrototype { get; }
    /// <summary>What seeds the header's circuit traces.</summary>
    string Seed { get; }
    ItemArt Art { get; }
    bool HasArt { get; }
    ImmutableArray<CompareValue> CoreStats { get; }
    bool HasCoreStats { get; }
    ImmutableArray<string> BrandBonuses { get; }
    bool HasBrandBonuses { get; }
}

/// <summary>
/// The picker's highlighted item in the inspector, as Go's candidate view: its header, where it is already equipped,
/// its core stats marked against the slot's current item, its brand or set's tiers with it equipped, and Equip.
/// </summary>
public sealed record CandidateViewModel(string Title, string HeaderCaption, string Accent, ItemArt Art, string Marker,
    ImmutableArray<CompareValue> CoreStats, ImmutableArray<string> BrandBonuses, IRelayCommand EquipCommand, ImmutableArray<CardLine> Card) : IItemSummary
{
    public bool HasAccent => Accent.Length > 0;
    public bool IsPrototype => false;
    public string Seed => Title;
    public bool HasArt => !Art.IsEmpty;
    public bool HasMarker => Marker.Length > 0;
    public bool HasCoreStats => !CoreStats.IsEmpty;
    public bool HasBrandBonuses => !BrandBonuses.IsEmpty;
}

/// <summary>
/// One line of an item's read-only card, as Go's buildItemDetails: a section header, a rolled attribute with its bar
/// (filled to where the roll sits in its range, in its category's colour), a talent with its icon, a caption, or text.
/// </summary>
public sealed record CardLine(CardLineKind Kind, string Text, double Fraction = 0, string Color = "", string? Icon = null)
{
    public bool IsHeader => Kind == CardLineKind.Header;
    public bool IsRoll => Kind == CardLineKind.Roll;
    public bool IsTalent => Kind == CardLineKind.Talent;
    public bool IsCaption => Kind == CardLineKind.Caption;
    public bool IsText => Kind == CardLineKind.Text;
    public bool HasColor => Color.Length > 0;
}

public enum CardLineKind { Header, Text, Roll, Talent, Caption }
