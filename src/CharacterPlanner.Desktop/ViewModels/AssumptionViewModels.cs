using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A section of the assumptions view, as the pack's layout names them, holding the assumptions in it.</summary>
public sealed class AssumptionSectionViewModel(string name, ImmutableArray<AssumptionViewModel> assumptions) : ViewModelBase
{

    public string Name { get; } = name;
    public ImmutableArray<AssumptionViewModel> Assumptions { get; } = assumptions;
    /// <summary>Whether any assumption in the section is shown (it applies and matches the search); an empty section is hidden.</summary>
    public bool HasVisible { get; private set => SetProperty(ref field, value); }

    internal void Update() => HasVisible = Assumptions.Any(assumption => assumption.IsShown);
}

/// <summary>
/// One assumption the user can set, as a card: a selector (a choice of values), a numeric input (labelled options or a
/// typed number), or a group of numeric inputs offered as one Off/On choice. It applies while the build reads it, or
/// while it is set, so a set value can always be put back to its default (F46); it is shown while it applies and
/// matches the search. Clearing returns it to the pack's default.
/// </summary>
public abstract class AssumptionViewModel : DraftValueViewModel
{
    private bool relevant;
    private bool set;
    private bool visible;
    private bool matches = true;
    private bool shown;

    protected AssumptionViewModel(EditorViewModel editor, string id, string name, string description) : base(editor)
    {
        Id = id;
        // Some assumptions are named by their id; show those as words.
        Name = name.Length == 0 || name == id ? Humanize(id) : name;
        Description = description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>The card's description, as the Go app shows it (some long pack texts have a shorter player-facing form).</summary>
    public virtual string ShownDescription => Description;
    public bool HasDescription => ShownDescription.Length > 0;
    /// <summary>What the search box matches: the name and description, as Go's.</summary>
    protected virtual string SearchText => Name + " " + Description;

    /// <summary>The assumption applies to this build: the build reads it, or it is set.</summary>
    public bool IsVisible { get => visible; private set { if (SetProperty(ref visible, value)) { UpdateShown(); } } }
    /// <summary>It applies and matches the search.</summary>
    public bool IsShown { get => shown; private set => SetProperty(ref shown, value); }

    /// <summary>A labelled dropdown; empty for a typed number.</summary>
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); } = [];
    public bool HasChoices => !Choices.IsDefaultOrEmpty;

    protected void SetChoices(ImmutableArray<Choice> choices) => Choices = choices;

    /// <summary>Adds a stored value the dropdown doesn't list, so it shows rather than blank.</summary>
    protected Choice Listed(Choice choice)
    {
        if (!Choices.Contains(choice)) { Choices = Choices.Add(choice); }
        return choice;
    }

    internal void Relevant(bool value)
    {
        relevant = value;
        IsVisible = relevant || set;
    }

    /// <summary>Whether the build reads it, from the assumptions a resolution read.</summary>
    internal void Relevant(ImmutableHashSet<string> read) => Relevant(Reads(read));

    protected virtual bool Reads(ImmutableHashSet<string> read) => read.Contains(Id);

    internal void Refresh(ImmutableDictionary<string, string> assumptions)
    {
        set = IsSet(assumptions);
        IsVisible = relevant || set;
        Show(assumptions);
    }

    protected virtual bool IsSet(ImmutableDictionary<string, string> assumptions) => assumptions.ContainsKey(Id);

    protected virtual void Show(ImmutableDictionary<string, string> assumptions) => Show(assumptions.TryGetValue(Id, out var value) ? value : null);

    protected abstract void Show(string? value);

    /// <summary>Narrows by the search box: every word must appear in the name or description (Go's matchesAllWords).</summary>
    internal void Filter(string query)
    {
        matches = TextSearch.MatchesAllWords(SearchText, query);
        UpdateShown();
    }

    private void UpdateShown() => IsShown = visible && matches;

    protected BuildOperation Cleared() => ContextOperation.Cleared(Id);

    internal static string Humanize(string id)
    {
        var words = id.Replace('_', ' ').Trim();
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// The pack's assumptions as cards, each with its section as the pack files it: every selector but the weapon in hand
    /// (the summary's weapon tabs choose that), then the numeric inputs, those sharing a group as one Off/On card (G339)
    /// filed where its first member is.
    /// </summary>
    internal static IEnumerable<(string Section, AssumptionViewModel Assumption)> Cards(EditorViewModel editor, GamePack pack)
    {
        foreach (var selector in pack.Selectors.Where(selector => selector.ID != GamePack.WeaponSlotSelector))
        { yield return (selector.Section, new SelectorAssumptionViewModel(editor, selector)); }
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in pack.NumericContextInputs)
        {
            if (input.Group.Length == 0)
            {
                var followsDistance = !pack.Simulation.DistanceDefaults.IsDefaultOrEmpty && pack.Simulation.DistanceDefaults.Any(entry => entry.Input == input.ID);
                yield return (input.Section, new NumericAssumptionViewModel(editor, input, followsDistance));
            }
            else if (groups.Add(input.Group))
            { yield return (input.Section, new GroupedAssumptionViewModel(editor, input.Group, [.. pack.NumericContextInputs.Where(member => member.Group == input.Group)])); }
        }
    }
}

/// <summary>A selector: the pack default, or one of its values.</summary>
public sealed class SelectorAssumptionViewModel : AssumptionViewModel
{
    private Choice selected;

    internal SelectorAssumptionViewModel(EditorViewModel editor, SelectorDefinition definition) : base(editor, definition.ID, definition.Name, definition.Description)
    {
        SetChoices([Default, .. definition.Values.Select(value => new Choice(value, value == "true" ? "On" : Humanize(value)))]);
        selected = Default;
    }

    public static readonly Choice Default = new("", "Default");

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            Editor.Apply(value.Id.Length == 0 ? Cleared() : new ContextOperation(Id, Text: value.Id));
        }
    }

    protected override BuildOperation? Parse(string text, out string problem) { problem = ""; return null; }

    protected override bool Committable => false;

    protected override void Show(string? value) =>
        Selected = value is null ? Default : Listed(Choices.FirstOrDefault(choice => choice.Id == value) ?? Choice.Unknown(value));
}

/// <summary>A numeric input: chosen from its labelled options when it has them, typed within its range otherwise.</summary>
public sealed class NumericAssumptionViewModel : AssumptionViewModel
{
    /// <summary>
    /// The Go app's shorter player-facing text for some skill comparisons (skillComparisonHelp); the pack's full source
    /// notes stay in its description, which the search still matches.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> ShortDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["overcharge_damage_preview"] = "0: Off; 1: On. Compare Overcharge damage. Ordinary damage stays unchanged.",
        ["overcharge_healing_preview"] = "0: Off; 1: On. Compare Overcharge healing. Ordinary healing stays unchanged.",
        ["overcharge_geometry_preview"] = "0: Off; 1: On. Compare Overcharge radius, range and cone angle.",
        ["overcharge_capacity_preview"] = "0: Off; 1: On. Compare Overcharge charges and capacity.",
        ["overcharge_lifetime_preview"] = "0: Off; 1: On. Compare Overcharge duration. Ordinary duration stays unchanged.",
        ["overcharge_status_preview"] = "0: Off; 1: On. Compare Overcharge status-effect durations.",
        ["overcharge_health_preview"] = "0: Off; 1: On. Compare Overcharge skill health.",
        ["overcharge_utility_preview"] = "0: Off; 1: On. Compare Overcharge utility effects.",
        ["overcharge_recovery_preview"] = "0: Off; 1: On. Compare Overcharge cooldown and ammo recovery.",
        ["restorer_duration_source_preview"] = "0: Off; 1: On. Compare Restorer duration with Expertise and the selected duration mod, before Nanite. Ordinary duration stays unchanged."
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private readonly NumericContextInputDefinition definition;
    // Unset, a simulated fight takes this input from its distance (R44). A value equal to the default is then a real choice, so
    // it's stored, and clearing the box goes back to following the distance.
    private readonly bool followsDistance;
    private Choice selected = Choice.None;

    internal NumericAssumptionViewModel(EditorViewModel editor, NumericContextInputDefinition definition, bool followsDistance = false) : base(editor, definition.ID, definition.Name, definition.Description)
    {
        this.definition = definition;
        this.followsDistance = followsDistance;
        SetChoices(definition.Options.IsDefaultOrEmpty ? [] : [.. definition.Options.Select(option => new Choice(Text(option.Value), option.Label))]);
        Range = $"{Shown(definition.Min)} – {Shown(definition.Max)}, default {Shown(definition.Default)}";
        Hint = $"Range {Shown(definition.Min)}–{Shown(definition.Max)} · step {Shown(definition.Step)}" + (followsDistance ? " · empty follows the distance" : "");
    }

    /// <summary>The legal range and the default, for a typed number.</summary>
    public string Range { get; }
    /// <summary>The range and step above a typed number's box, as Go's ("Range 0–100 · step 1").</summary>
    public string Hint { get; }

    public override string ShownDescription => ShortDescriptions.GetValueOrDefault(Id, Description);

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            if (!double.TryParse(value.Id, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { return; }
            // Choosing the default stores nothing, so the build follows the pack if its default changes.
            Editor.Apply(number == definition.Default && !followsDistance ? Cleared() : new ContextOperation(Id, Value: number));
        }
    }

    protected override bool Committable => !HasChoices;

    protected override BuildOperation? Parse(string text, out string problem)
    {
        problem = $"{Name} must be a number from {Range}";
        if (followsDistance && text.Trim().Length == 0) { return Cleared(); }
        if (Formatting.Parse(StatFormats.Decimal, text) is not { } number || !definition.Accepts(number)) { return null; }
        return number == definition.Default && !followsDistance ? Cleared() : new ContextOperation(Id, Value: number);
    }

    protected override void Show(string? value)
    {
        var number = Stored(definition, value);
        if (HasChoices) { Selected = Listed(Choices.FirstOrDefault(choice => choice.Id == Text(number)) ?? Choice.Unknown(Text(number))); }
        else { ShowValue(followsDistance && value is null ? "" : Formatting.Input(StatFormats.Decimal, number)); }
    }

    /// <summary>An input's value: assumptions travel as invariant text, and an absent one is the pack's default.</summary>
    internal static double Stored(NumericContextInputDefinition input, string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : input.Default;

    /// <summary>An option's id: the stored value as invariant text, as assumptions travel.</summary>
    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A value as people read it, in the current culture.</summary>
    private static string Shown(double value) => Formatting.Value(StatFormats.Decimal, value);
}

/// <summary>
/// Numeric inputs sharing a group, offered as one Off/On card (Go's G339): On sets every member to its maximum and Off
/// clears them back to their defaults, each in one undo step. It reads On only while every member is at its maximum,
/// and applies while the build reads any member or any is set.
/// </summary>
public sealed class GroupedAssumptionViewModel : AssumptionViewModel
{
    public static readonly Choice Off = new("off", "Off");
    public static readonly Choice On = new("on", "On");

    private readonly ImmutableArray<NumericContextInputDefinition> members;
    private Choice selected = Off;

    internal GroupedAssumptionViewModel(EditorViewModel editor, string group, ImmutableArray<NumericContextInputDefinition> members)
        : base(editor, group, members[0].GroupName.Length > 0 ? members[0].GroupName : Humanize(group),
            $"Shows all {members.Length} {(members[0].GroupName.Length > 0 ? members[0].GroupName : Humanize(group))} outputs for the selected skills. Ordinary values stay unchanged.")
    {
        this.members = members;
        SetChoices([Off, On]);
    }

    /// <summary>The inputs the card sets, by id.</summary>
    public ImmutableArray<string> Members => [.. members.Select(member => member.ID)];

    /// <summary>Go searched a group card by its name and its first member's name.</summary>
    protected override string SearchText => Name + " " + members[0].Name;

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            var current = Editor.Session.Snapshot.State.Assumptions;
            // Only what changes; a maximum that is the default is stored as nothing, as a typed default is.
            BuildOperation[] batch = value == On
                ? [.. members.Where(member => NumericAssumptionViewModel.Stored(member, current.GetValueOrDefault(member.ID)) != member.Max)
                    .Select(member => member.Max == member.Default ? ContextOperation.Cleared(member.ID) : new ContextOperation(member.ID, Value: member.Max))]
                : [.. members.Where(member => current.ContainsKey(member.ID)).Select(member => ContextOperation.Cleared(member.ID))];
            if (batch.Length > 0) { Editor.Apply(batch); }
        }
    }

    protected override bool Reads(ImmutableHashSet<string> read) => members.Any(member => read.Contains(member.ID));

    protected override bool IsSet(ImmutableDictionary<string, string> assumptions) => members.Any(member => assumptions.ContainsKey(member.ID));

    protected override void Show(ImmutableDictionary<string, string> assumptions) =>
        Selected = members.All(member => NumericAssumptionViewModel.Stored(member, assumptions.GetValueOrDefault(member.ID)) == member.Max) ? On : Off;

    protected override void Show(string? value) { }

    protected override BuildOperation? Parse(string text, out string problem) { problem = ""; return null; }

    protected override bool Committable => false;
}
