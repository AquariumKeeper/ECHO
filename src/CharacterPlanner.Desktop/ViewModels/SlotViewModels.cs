using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A named group of equipment slots, as the pack's layout arranges them (weapons, gear).</summary>
public sealed record SlotGroupViewModel(string Name, ImmutableArray<SlotViewModel> Slots);

/// <summary>
/// One equipment slot: its item (chosen in the item picker), and for that item its attribute, mod and talent pickers,
/// its upgrade grade and its Prototype quality. Choosing something applies one operation through the editor; showing a
/// new state never does, so a refresh can't echo an edit.
/// </summary>
public sealed class SlotViewModel : ViewModelBase, IItemSummary
{
    /// <summary>The augment chooser's "No augment" (Prototype without an augment).</summary>
    public static readonly Choice NoAugment = new("", "No augment");

    private readonly EditorViewModel editor;
    private string itemId = "";
    private ImmutableArray<AttributeViewModel> attributes = [];
    private ImmutableArray<ModViewModel> mods = [];
    private ImmutableArray<TalentViewModel> talents = [];
    private RankViewModel? upgrade;
    private Choice augment = NoAugment;
    private Choice level = Choice.None;
    private ImmutableArray<Choice> levels = [];
    private string headerCaption = "";
    private string hint = "Choose an item to equip in this slot.";

    internal SlotViewModel(EditorViewModel editor, SlotDefinition slot, ImmutableArray<StatDefinition> compareStats)
    {
        this.editor = editor;
        Id = slot.ID;
        Name = slot.Name;
        CompareStats = compareStats;
        Entries = [.. editor.Pack.Items.Where(item => item.Slot == slot.ID)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(editor.EntryOf)];
        BrowseCommand = new RelayCommand(() => editor.Browse(this));
        UnequipCommand = new RelayCommand(() => editor.Apply(EquipmentOperation.Cleared(Id)), () => IsEquipped);
        headerCaption = rarityCaption = Name;
        Augments = editor.Pack.Prototype is { } definition ? [NoAugment, .. definition.Augments.Select(entry => new Choice(entry.ID, entry.Name))] : [];
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>The slot's items, by name.</summary>
    internal ImmutableArray<ItemEntry> Entries { get; }
    /// <summary>The stats the item picker compares for this slot, as the layout's slot group names them.</summary>
    internal ImmutableArray<StatDefinition> CompareStats { get; }
    /// <summary>Opens the item picker for this slot.</summary>
    public IRelayCommand BrowseCommand { get; }
    /// <summary>Opens Rank Upgrades for this slot's item (the editor's command, with <see cref="Id"/>).</summary>
    public IRelayCommand<string> RankUpgradesCommand => editor.RankUpgradesCommand;
    public IRelayCommand UnequipCommand { get; }

    // The inspector's view of the slot, as the Go app's: the item's header, Choose or Change item and Unequip, its core
    // stats, its configuration, its brand and set bonuses, and a hint when there is nothing to configure.

    /// <summary>The item's name, or "Empty".</summary>
    public string Title { get; private set => SetProperty(ref field, value); } = "Empty";
    /// <summary>The slot and the item's rarity: "Mask · High-End".</summary>
    public string HeaderCaption { get => headerCaption; private set => SetProperty(ref headerCaption, value); }
    public string Accent { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasAccent)); } } } = "";
    public bool HasAccent => Accent.Length > 0;
    /// <summary>What seeds the header's circuit traces: the slot, as its tile's.</summary>
    public string Seed => Name;
    private string rarityCaption = "";
    private string rarityAccent = "";
    public ItemArt Art { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasArt)); } } } = ItemArt.None;
    public bool HasArt => !Art.IsEmpty;
    public bool IsEquipped => itemId.Length > 0;
    public string ChooseText => IsEquipped ? "Change item" : "Choose item";
    /// <summary>"Choose an item to equip in this slot.", or that the item has nothing to configure, or "".</summary>
    public string Hint { get => hint; private set { if (SetProperty(ref hint, value)) { OnPropertyChanged(nameof(HasHint)); } } }
    public bool HasHint => Hint.Length > 0;
    /// <summary>The slot group's compared stats with this item, in the slot's context (a weapon as the weapon in hand).</summary>
    public ImmutableArray<CompareValue> CoreStats { get; internal set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasCoreStats)); } } } = [];
    public bool HasCoreStats => !CoreStats.IsEmpty;
    /// <summary>The item's brand or gear set: how many pieces count, then each tier, active or not, with its fixed effects.</summary>
    public ImmutableArray<string> BrandBonuses { get; internal set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasBrandBonuses)); } } } = [];
    public bool HasBrandBonuses => !BrandBonuses.IsEmpty;
    /// <summary>Attribute slots whose choices are all core attributes, shown first as Go's "Core attributes".</summary>
    public ImmutableArray<AttributeViewModel> CoreAttributes { get; private set; } = [];
    public ImmutableArray<AttributeViewModel> OtherAttributes { get; private set; } = [];
    public bool HasCoreAttributes => !CoreAttributes.IsEmpty;
    public bool HasOtherAttributes => !OtherAttributes.IsEmpty;
    public bool HasMods => !Mods.IsDefaultOrEmpty;
    public bool HasTalents => !Talents.IsDefaultOrEmpty;
    /// <summary>The item has attributes, mods or talents to rank changes of (Go's Rank Upgrades).</summary>
    public bool CanRank => !Attributes.IsDefaultOrEmpty || HasMods || HasTalents;
    /// <summary>The item's upgrade's description, for the inspector's upgrade section.</summary>
    public string UpgradeDescription { get; private set; } = "";

    /// <summary>The equipped item's id, or "".</summary>
    public string ItemId => itemId;

    /// <summary>Equips an item in the slot, as the picker's Equip does; false (with the reason in the message line) when refused.</summary>
    public bool Equip(string itemId) => editor.Apply(new EquipmentOperation(Id, itemId));

    public ImmutableArray<AttributeViewModel> Attributes { get => attributes; private set => SetProperty(ref attributes, value); }
    public ImmutableArray<ModViewModel> Mods { get => mods; private set => SetProperty(ref mods, value); }
    public ImmutableArray<TalentViewModel> Talents { get => talents; private set => SetProperty(ref talents, value); }

    /// <summary>The item's upgrade grade, or null when the item has none.</summary>
    public RankViewModel? Upgrade { get => upgrade; private set => SetProperty(ref upgrade, value); }

    /// <summary>Whether the equipped item can be Prototype.</summary>
    public bool PrototypeEligible { get; private set => SetProperty(ref field, value); }
    // Prototype quality, as Go's section (G392): the check, the augment (enabled once Prototype) and its level (once
    // there is an augment), and what the augment does at that level.

    /// <summary>"Prototype quality": checking it makes the item Prototype without an augment; clearing it, normal.</summary>
    public bool IsPrototype
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value) || editor.Refreshing) { return; }
            editor.Apply(value ? new PrototypeOperation(Id) : PrototypeOperation.Cleared(Id));
        }
    }

    /// <summary>"No augment", then each augment.</summary>
    public ImmutableArray<Choice> Augments { get; private set => SetProperty(ref field, value); }

    public Choice Augment
    {
        get => augment;
        set
        {
            if (value is null || !SetProperty(ref augment, value) || value.IsUnknown || editor.Refreshing) { return; }
            // A new augment starts at its highest level.
            editor.Apply(value.Id.Length == 0 ? new PrototypeOperation(Id) : new PrototypeOperation(Id, value.Id));
        }
    }

    /// <summary>The selected augment's levels, 1 to its highest.</summary>
    public ImmutableArray<Choice> Levels { get => levels; private set => SetProperty(ref levels, value); }

    public Choice Level
    {
        get => level;
        set
        {
            if (value is null || !SetProperty(ref level, value) || value.IsUnknown || editor.Refreshing || augment.Id.Length == 0) { return; }
            if (long.TryParse(value.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) { editor.Apply(new PrototypeOperation(Id, augment.Id, number)); }
        }
    }

    public bool HasAugment => IsPrototype && augment.Id.Length > 0;
    /// <summary>The augment's description with its value at this level ("Increases … by 12%").</summary>
    public string AugmentEffect { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasAugmentEffect)); } } } = "";
    public bool HasAugmentEffect => AugmentEffect.Length > 0;

    internal IEnumerable<IDraftValue> Drafts =>
        Attributes.Cast<IDraftValue>().Concat(Mods).Concat(new IDraftValue?[] { Upgrade }.OfType<IDraftValue>());

    internal void RefreshArtwork()
    {
        Art = editor.Items.TryGetValue(itemId, out var item) ? editor.Artwork.Item(item.ID, Id) : ItemArt.None;
        foreach (var talent in Talents) { talent.RefreshArtwork(); }
    }

    internal void Refresh(BuildSnapshot build)
    {
        var id = build.Equipment.GetValueOrDefault(Id, "");
        var item = editor.Items.GetValueOrDefault(id);
        if (id != itemId || attributes.IsDefault)
        {
            itemId = id;
            // Another item has other attribute, mod and talent slots and another upgrade, so its pickers are built afresh.
            Attributes = item is null ? [] : [.. item.AttributeSlots.Select(slot => new AttributeViewModel(editor, Id, item, slot))];
            Mods = item is null ? [] : [.. item.ModSlots.Select(slot => new ModViewModel(editor, Id, slot))];
            Talents = item is null ? [] : [.. item.TalentSlots.Select(slot => new TalentViewModel(editor, Id, slot))];
            // A slot whose choices are all core attributes is a core slot, shown first.
            bool IsCore(ItemAttributeSlotDefinition slot) => !slot.Choices.IsEmpty && slot.Choices.All(choice => editor.Attributes.GetValueOrDefault(choice.AttributeID)?.Core == true);
            CoreAttributes = item is null ? [] : [.. Attributes.Where(attribute => IsCore(item.AttributeSlots.First(slot => slot.ID == attribute.Id)))];
            OtherAttributes = [.. Attributes.Except(CoreAttributes)];
            UpgradeDescription = item?.Upgrade?.Description ?? "";
            var entry = item is null ? null : editor.EntryOf(item);
            Title = item?.Name ?? (id.Length == 0 ? "Empty" : Choice.Unknown(id).Name);
            rarityCaption = entry?.Rarity is { } rarity ? $"{Name} · {rarity.Name}" : Name;
            rarityAccent = entry?.Rarity?.Color ?? "";
            Art = item is null ? ItemArt.None : editor.Artwork.Item(item.ID, Id);
            var configurable = item is not null && item.AttributeSlots.Length + item.ModSlots.Length + item.TalentSlots.Length > 0 || item?.Upgrade is not null;
            Hint = item is null ? "Choose an item to equip in this slot." : configurable ? "" : "This item has no attributes, mods, or talents to configure.";
            foreach (var name in new[] { nameof(ItemId), nameof(IsEquipped), nameof(ChooseText), nameof(HasCoreAttributes), nameof(HasOtherAttributes), nameof(HasMods), nameof(HasTalents), nameof(CanRank), nameof(CoreAttributes), nameof(OtherAttributes), nameof(UpgradeDescription) })
            { OnPropertyChanged(name); }
            UnequipCommand.NotifyCanExecuteChanged();
            Upgrade = item?.Upgrade is { } definition
                ? new RankViewModel(editor, id, definition.Name.Length > 0 ? definition.Name : "Upgrade", 0, definition.MaxRank, rank => new EquipmentUpgradeOperation(Id, id, rank))
                : null;
            PrototypeEligible = item is not null && PrototypeRules.Eligible(editor.Pack, item, Id);
        }
        foreach (var attribute in Attributes) { attribute.Refresh(build); }
        foreach (var mod in Mods) { mod.Refresh(build); }
        foreach (var talent in Talents) { talent.Refresh(build); }
        var installed = build.EquipmentUpgrades.GetValueOrDefault(Id);
        Upgrade?.Refresh(installed is not null && installed.ItemId == id ? installed.Rank : 0);
        RefreshPrototype(build, id);
    }

    private void RefreshPrototype(BuildSnapshot build, string id)
    {
        var selection = build.Prototypes.GetValueOrDefault(Id);
        var active = selection is not null && selection.ItemId == id && id.Length > 0;
        IsPrototype = active;
        // A Prototype item's header turns Prototype purple and says so; the tile does the same.
        HeaderCaption = active ? rarityCaption + " · Prototype" : rarityCaption;
        Accent = PrototypeStyle.Accent(active, rarityAccent, PrototypeStyle.Color(editor.Pack));
        OnPropertyChanged(nameof(Seed));
        Augment = active && selection!.Augment.Length > 0 ? Listed(Augments, selection.Augment, list => Augments = list) : NoAugment;
        var definition = active ? editor.Pack.Prototype?.Augments.FirstOrDefault(entry => entry.ID == selection!.Augment) : null;
        Levels = definition is null ? [] : [.. Enumerable.Range(1, (int)definition.Levels).Select(number => new Choice(number.ToString(CultureInfo.InvariantCulture), number.ToString(CultureInfo.CurrentCulture)))];
        Level = definition is null ? Choice.None : Levels.FirstOrDefault(entry => entry.Id == selection!.Level.ToString(CultureInfo.InvariantCulture)) ?? Choice.None;
        AugmentEffect = definition is null ? "" : definition.Description.Replace("{}", Formatting.Value(StatFormats.Percent, definition.Base + definition.PerLevel * (selection!.Level - 1)), StringComparison.Ordinal);
        OnPropertyChanged(nameof(HasAugment));
    }
}

/// <summary>An attribute or mod roll, typed in display units (a stored 0.15 percent reads 15).</summary>
public abstract class RollViewModel : DraftValueViewModel
{
    private string range = "";

    protected RollViewModel(EditorViewModel editor) : base(editor) =>
        MaximumCommand = new RelayCommand(() => { if (Highest() is { } operation) { Editor.Apply(operation); } }, () => HasRoll && CanChange);

    /// <summary>Sets the roll to its highest, as Go's Max.</summary>
    public IRelayCommand MaximumCommand { get; }

    /// <summary>The operation that sets the roll to its highest, or null when there is nothing rolled.</summary>
    protected abstract BuildOperation? Highest();

    /// <summary>Whether the roll can be changed now (a skill's mods can't while the skill isn't accepted).</summary>
    public bool IsEnabled
    {
        get => enabled;
        protected set { if (SetProperty(ref enabled, value)) { MaximumCommand.NotifyCanExecuteChanged(); } }
    }
    private bool enabled = true;

    private bool CanChange => IsEnabled;

    /// <summary>What the roll's controls are called, for assistive technology ("Core 1", "Mod 1 roll").</summary>
    public abstract string RollLabel { get; }

    /// <summary>The roll on a slider, as Go's: dragging snaps to the legal steps and applies, one undo step a drag.</summary>
    public double SliderValue
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value) || Editor.Refreshing || Bounds is not { } bounds) { return; }
            var roll = Math.Clamp(value, bounds.Min, bounds.Max);
            if (bounds.Step is { } step && step > 0) { roll = Math.Clamp(bounds.Min + Math.Round((roll - bounds.Min) / step) * step, bounds.Min, bounds.Max); }
            Editor.ApplyCoalescing(SlideKey, Operation(bounds.Snap(roll)));
        }
    }

    public double SliderMinimum => Bounds?.Min ?? 0;
    public double SliderMaximum => Bounds?.Max ?? 1;
    public double SliderStep => Bounds?.Step ?? 0;
    /// <summary>The roll has a range to slide over (a fixed roll has none).</summary>
    public bool CanSlide => Bounds is { } bounds && bounds.Max > bounds.Min;

    /// <summary>Which roll a slide changes, so a drag's steps coalesce into one undo step.</summary>
    protected abstract string SlideKey { get; }

    protected string Format { get; set; } = StatFormats.Decimal;
    protected AttributeRollBounds? Bounds { get; set; }

    /// <summary>The legal range, as "min – max".</summary>
    public string Range { get => range; private set => SetProperty(ref range, value); }
    public bool HasRoll { get; private set => SetProperty(ref field, value); }

    protected override bool Committable => HasRoll;

    protected override BuildOperation? Parse(string text, out string problem)
    {
        problem = $"\"{text}\" isn't a number";
        return Formatting.Parse(Format, text) is { } value ? Operation(value) : null;
    }

    protected abstract BuildOperation Operation(double value);

    protected void ShowRoll(AttributeRollBounds? bounds, string format, double? value)
    {
        (Bounds, Format, HasRoll) = (bounds, format, bounds is not null);
        Range = bounds is { } legal ? $"{Formatting.Value(format, legal.Min)} – {Formatting.Value(format, legal.Max)}" : "";
        ShowValue(value is { } current ? Formatting.Input(format, current) : "");
        foreach (var name in new[] { nameof(SliderMinimum), nameof(SliderMaximum), nameof(SliderStep), nameof(CanSlide) }) { OnPropertyChanged(name); }
        MaximumCommand.NotifyCanExecuteChanged();
        // Shown while the editor refreshes, so the slider's setter applies nothing back.
        SliderValue = value ?? bounds?.Min ?? 0;
    }
}

/// <summary>A core or minor attribute slot: which attribute it rolls, and the roll.</summary>
public sealed class AttributeViewModel : RollViewModel
{
    private readonly string slotId;
    private readonly ItemDefinition item;
    private readonly ItemAttributeSlotDefinition definition;
    // Every choice the slot allows; Choices leaves out those another slot of the item holds.
    private readonly ImmutableArray<Choice> allowed;
    private Choice selected = Choice.None;

    internal AttributeViewModel(EditorViewModel editor, string slotId, ItemDefinition item, ItemAttributeSlotDefinition definition) : base(editor)
    {
        (this.slotId, this.item, this.definition) = (slotId, item, definition);
        (Id, Name) = (definition.ID, definition.Name);
        allowed = [Choice.None, .. definition.Choices.Select(choice => new Choice(choice.AttributeID, editor.Attributes.GetValueOrDefault(choice.AttributeID)?.Name ?? choice.AttributeID))];
        Choices = allowed;
    }

    /// <summary>The attribute slot's id within its item (core_1, minor_1, ...).</summary>
    public string Id { get; }
    public string Name { get; }
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            // A new attribute starts at its highest legal roll.
            Editor.Apply(value.Id.Length == 0 ? AttributeOperation.Cleared(slotId, definition.ID) : new AttributeOperation(slotId, definition.ID, value.Id));
        }
    }

    protected override BuildOperation Operation(double value) => new AttributeOperation(slotId, definition.ID, selected.Id, value);

    protected override string SlideKey => $"attribute {slotId} {definition.ID}";

    protected override BuildOperation? Highest() => selected.Id.Length > 0 ? new AttributeOperation(slotId, definition.ID, selected.Id) : null;

    public override string RollLabel => Name;


    internal void Refresh(BuildSnapshot build)
    {
        var rolls = build.AttributeRolls.GetValueOrDefault(slotId);
        var roll = rolls?.GetValueOrDefault(definition.ID);
        // An item's rolled attributes are distinct (R40): the item's other slots' attributes aren't offered here.
        // The slot's own roll stays listed, even when another slot repeats it (a saved build) or the pack lacks it.
        var taken = rolls?.Where(pair => pair.Key != definition.ID).Select(pair => pair.Value.AttributeId).ToHashSet(StringComparer.Ordinal) ?? [];
        var current = Choice.Of(allowed, roll?.AttributeId ?? "");
        ImmutableArray<Choice> offered = [.. allowed.Where(choice => choice == current || !taken.Contains(choice.Id))];
        if (!offered.Contains(current)) { offered = offered.Add(current); }
        if (!offered.SequenceEqual(Choices)) { Choices = offered; }
        Selected = current;
        var attribute = roll is null ? null : Editor.Attributes.GetValueOrDefault(roll.AttributeId);
        var choice = roll is null ? null : definition.Choices.FirstOrDefault(choice => choice.AttributeID == roll.AttributeId);
        var bounds = attribute is null || choice is null ? (AttributeRollBounds?)null
            : PrototypeRules.RollBounds(attribute, choice, PrototypeRules.IsPrototype(Editor.Pack, build, slotId, item));
        ShowRoll(bounds, attribute?.Format ?? StatFormats.Decimal, roll?.Value);
    }
}

/// <summary>A mod slot: which mod, and its roll when the mod has one.</summary>
public sealed class ModViewModel : RollViewModel
{
    private readonly string slotId;
    private readonly ItemModSlotDefinition definition;
    private Choice selected = Choice.None;

    internal ModViewModel(EditorViewModel editor, string slotId, ItemModSlotDefinition definition) : base(editor)
    {
        (this.slotId, this.definition) = (slotId, definition);
        (Id, Name) = (definition.ID, definition.Name);
        Choices = [Choice.None, .. definition.Choices.Select(id => new Choice(id, editor.Mods.GetValueOrDefault(id) is { } mod ? Label(editor, mod) : id))];
        RollName = $"{definition.Name} roll";
    }

    /// <summary>The roll row's name, as Go's ("Mod 1 roll").</summary>
    public string RollName { get; }
    /// <summary>The selected mod's description, or "".</summary>
    public string Description { get; private set; } = "";
    public bool HasDescription => Description.Length > 0;

    /// <summary>A mod by what it does, as Go's modChoiceLabel: "Name · +50% Optimal Range" or "Name · 3%–6% Critical Hit Chance".</summary>
    internal static string Label(EditorViewModel editor, ModDefinition mod)
    {
        var effects = new List<string>();
        if (editor.Attributes.GetValueOrDefault(mod.RollAttributeID) is { } roll)
        {
            var name = editor.Statistics.GetValueOrDefault(roll.Stat)?.Name ?? roll.Name;
            effects.Add(roll.Min == roll.Max ? $"+{Formatting.Value(roll.Format, roll.Max)} {name}" : $"{Formatting.Value(roll.Format, roll.Min)}–{Formatting.Value(roll.Format, roll.Max)} {name}");
        }
        // Only fixed, unconditional flat modifiers read as an effect.
        foreach (var modifier in mod.Modifiers.Where(modifier => modifier.Op == ModifierOps.Flat && !modifier.Value.IsExpression && modifier.RequiresContext is null && modifier.When.Length == 0))
        {
            if (editor.Statistics.GetValueOrDefault(modifier.Stat) is { } stat)
            { effects.Add($"{(modifier.Value.Number >= 0 ? "+" : "")}{Formatting.Value(stat.Format, modifier.Value.Number)} {stat.Name}"); }
        }
        return effects.Count == 0 ? mod.Name : $"{mod.Name} · {string.Join(", ", effects)}";
    }

    /// <summary>The mod slot's id within its item.</summary>
    public string Id { get; }
    public string Name { get; }
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            Editor.Apply(value.Id.Length == 0 ? ModOperation.Cleared(slotId, definition.ID) : new ModOperation(slotId, definition.ID, value.Id));
        }
    }

    protected override BuildOperation Operation(double value) => new ModRollOperation(slotId, definition.ID, value);

    protected override string SlideKey => $"mod roll {slotId} {definition.ID}";

    protected override BuildOperation? Highest() => new ModRollOperation(slotId, definition.ID);

    public override string RollLabel => RollName;

    internal void Refresh(BuildSnapshot build)
    {
        var id = build.ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(definition.ID) ?? "";
        Selected = Listed(Choices, id, list => Choices = list);
        var mod = Editor.Mods.GetValueOrDefault(id);
        Description = mod?.Description ?? "";
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        var attribute = mod is null ? null : Editor.Attributes.GetValueOrDefault(mod.RollAttributeID);
        double? value = build.ModRolls.GetValueOrDefault(slotId)?.TryGetValue(definition.ID, out var roll) == true ? roll : null;
        ShowRoll(attribute is null ? null : new AttributeRollBounds(attribute.Min, attribute.Max, attribute.Step), attribute?.Format ?? StatFormats.Decimal, value);
    }
}

/// <summary>A talent slot.</summary>
public sealed class TalentViewModel : ViewModelBase
{
    private readonly EditorViewModel editor;
    private readonly string slotId;
    private readonly ItemTalentSlotDefinition definition;
    private Choice selected = Choice.None;

    internal TalentViewModel(EditorViewModel editor, string slotId, ItemTalentSlotDefinition definition)
    {
        (this.editor, this.slotId, this.definition) = (editor, slotId, definition);
        (Id, Name) = (definition.ID, definition.Name);
        Choices = [Choice.None, .. definition.Choices.Select(id => new Choice(id, editor.Talents.GetValueOrDefault(id)?.Name ?? id))];
    }

    /// <summary>Opens Compare talents for this talent slot.</summary>
    public IRelayCommand CompareCommand => compare ??= new RelayCommand(() => editor.CompareTalents(slotId, definition.ID));
    private IRelayCommand? compare;

    /// <summary>The selected talent's description, with the set credit it adds and the sets its modeled effects need, as Go's.</summary>
    public string Description { get; private set; } = "";
    public bool HasDescription => Description.Length > 0;
    /// <summary>The selected talent's icon file, or null.</summary>
    public string? Icon { get; private set; }

    /// <summary>The talent slot's id within its item.</summary>
    public string Id { get; }
    public string Name { get; }
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || editor.Refreshing) { return; }
            editor.Apply(value.Id.Length == 0 ? TalentOperation.Cleared(slotId, definition.ID) : new TalentOperation(slotId, definition.ID, value.Id));
        }
    }


    internal void RefreshArtwork()
    {
        Icon = editor.Artwork.Icon("talents", Selected.Id);
        OnPropertyChanged(nameof(Icon));
    }

    internal void Refresh(BuildSnapshot build)
    {
        var id = build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(definition.ID) ?? "";
        Selected = Listed(Choices, id, list => Choices = list);
        var talent = editor.Talents.GetValueOrDefault(id);
        var notes = new List<string>();
        if (talent is { Description.Length: > 0 }) { notes.Add(talent.Description); }
        if (talent is { SetPieceCredit: > 0 }) { notes.Add($"Adds {talent.SetPieceCredit} piece credit to each equipped set automatically."); }
        if (talent is { ModeledBySets.IsEmpty: false })
        {
            var sets = talent.ModeledBySets.Select(set => editor.Pack.Sets.FirstOrDefault(entry => entry.ID == set)?.Name ?? set);
            notes.Add($"Modeled effects require {string.Join(", ", sets)} set bonuses.");
        }
        Description = string.Join("\n\n", notes);
        Icon = talent is null ? null : editor.Artwork.Icon("talents", talent.ID);
        foreach (var name in new[] { nameof(Description), nameof(HasDescription), nameof(Icon) }) { OnPropertyChanged(name); }
    }
}
