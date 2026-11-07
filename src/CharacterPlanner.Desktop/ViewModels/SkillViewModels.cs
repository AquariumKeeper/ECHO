using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>One preview output of a skill: its value, and what it was worked out from.</summary>
public sealed record SkillOutputViewModel(string Name, string Value, ImmutableArray<string> Details)
{
    public string Text => $"{Name}: {Value}";
}

/// <summary>
/// One active-skill slot, as the Go app's skill panel: the skill, its Expertise grade, its mods and their rolls, and
/// its modeled preview (F20, F22, F23). Choosing something applies one operation through the editor; showing a new
/// state never does.
/// </summary>
public sealed class SkillSlotViewModel : ViewModelBase
{
    private readonly EditorViewModel editor;
    private string skillId = "";
    private Choice selected = Choice.None;
    private ImmutableArray<SkillModViewModel> mods = [];
    private bool accepted;
    private ImmutableArray<SkillOutputViewModel> outputs = [];

    internal SkillSlotViewModel(EditorViewModel editor, ActiveSkillSlotDefinition slot)
    {
        this.editor = editor;
        (Id, Name) = (slot.ID, slot.Name);
        Choices = [Choice.None, .. slot.Choices.Select(id => new Choice(id, editor.Skills.GetValueOrDefault(id)?.Name ?? id))];
    }

    public string Id { get; }
    public string Name { get; }
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || editor.Refreshing) { return; }
            editor.Apply(value.Id.Length == 0 ? ActiveSkillOperation.Cleared(Id) : new ActiveSkillOperation(Id, value.Id));
        }
    }

    /// <summary>The skill's Expertise grade, or null when it has none.</summary>
    public RankViewModel? Expertise { get; private set => SetProperty(ref field, value); }
    public ImmutableArray<SkillModViewModel> Mods { get => mods; private set => SetProperty(ref mods, value); }

    /// <summary>Whether the loadout accepts the skill; mods can be changed only then, as the resolver ignores them otherwise.</summary>
    public bool IsAccepted { get => accepted; private set => SetProperty(ref accepted, value); }

    /// <summary>The skill's icon file, or null.</summary>
    public string? Icon { get; private set; }
    public string Description { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasDescription)); } } } = "";
    public bool HasDescription => Description.Length > 0;
    /// <summary>Why there is no preview to show, or "".</summary>
    public string Status { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasStatus)); } } } = "";
    public bool HasStatus => Status.Length > 0;
    public ImmutableArray<SkillOutputViewModel> Outputs { get => outputs; private set => SetProperty(ref outputs, value); }

    internal IEnumerable<IDraftValue> Drafts => Mods.Cast<IDraftValue>().Concat(Expertise is null ? [] : [Expertise]);

    internal void RefreshArtwork()
    {
        Icon = editor.Artwork.Icon("skills", skillId);
        OnPropertyChanged(nameof(Icon));
    }

    internal void Refresh(BuildSnapshot build)
    {
        var id = build.ActiveSkillSelections.GetValueOrDefault(Id, "");
        var skill = editor.Skills.GetValueOrDefault(id);
        if (id != skillId || mods.IsDefault)
        {
            skillId = id;
            // Another skill has another grade and other mod slots, so their controls are built afresh.
            Expertise = skill?.Upgrade is { } upgrade
                ? new RankViewModel(editor, skill.ID, upgrade.Name.Length > 0 ? upgrade.Name : "Expertise", 0, upgrade.MaxRank, rank => new SkillUpgradeOperation(Id, rank))
                : null;
            Mods = skill is null ? [] : [.. skill.ModSlots.Select(slot => new SkillModViewModel(editor, Id, skill.ID, slot))];
            Description = skill?.Description ?? "";
            Icon = skill is null ? null : editor.Artwork.Icon("skills", skill.ID);
            OnPropertyChanged(nameof(Icon));
        }
        Selected = Listed(Choices, id, list => Choices = list);
        var installed = build.SkillUpgrades.GetValueOrDefault(Id);
        Expertise?.Refresh(installed is not null && installed.SkillId == id ? installed.Rank : 0);
        foreach (var mod in Mods) { mod.Refresh(build); }
    }

    /// <summary>Shows the resolved preview, or with null (the build couldn't be resolved) none.</summary>
    internal void ShowPreview(ResolvedBuild? resolved)
    {
        var skill = editor.Skills.GetValueOrDefault(skillId);
        var preview = resolved?.ActiveSkillPreviews.GetValueOrDefault(Id);
        IsAccepted = skill is not null && preview?.SkillId == skill.ID;
        foreach (var mod in Mods) { mod.Accept(IsAccepted); }
        Outputs = IsAccepted ? [.. preview!.Outputs.Select(output => Output(skill!, output))] : [];
        Status = skillId.Length == 0 ? "Select a skill to see its modeled preview."
            : resolved is null ? ""
            : skill is null || !IsAccepted ? $"{skill?.Name ?? skillId} is not accepted in this loadout."
            : !Outputs.IsEmpty ? ""
            : skill.PreviewOutputs.IsEmpty ? $"{skill.Name} has no modeled preview outputs."
            : resolved.Diagnostics.Any(diagnostic => diagnostic.Code == "active-skill-preview-eval" && diagnostic.Subject.StartsWith(Id + ".", StringComparison.Ordinal))
                ? "Preview outputs could not be calculated; see the diagnostics."
                : "No preview outputs apply to the current conditions.";
    }

    /// <summary>An output with what it uses: stats, the installed grade and mod rolls by name, then its formula.</summary>
    private SkillOutputViewModel Output(ActiveSkillDefinition skill, SkillPreviewOutput output)
    {
        var details = output.Inputs.OrderBy(input => input.Key, StringComparer.Ordinal).Select(input =>
        {
            if (editor.Statistics.GetValueOrDefault(input.Key) is { } stat) { return $"Uses {stat.Name}: {Formatting.Value(stat.Format, input.Value)}"; }
            if (input.Key == "skill_upgrade_rank") { return $"Uses installed grade: {Formatting.Value(StatFormats.Decimal, input.Value)}"; }
            var mod = skill.ModSlots.SelectMany(slot => slot.Choices).FirstOrDefault(choice => choice.Input == input.Key);
            if (mod is null) { return $"Uses {input.Key}: {Formatting.Value(StatFormats.Decimal, input.Value)}"; }
            // Two slots can roll one input, as Explosive Sticky Bomb's Blast Radius does, so name what's installed.
            var installed = Mods.Select(slot => slot.Installed).OfType<ActiveSkillModDefinition>().Where(choice => choice.Input == input.Key).Select(choice => choice.Name).ToArray();
            return $"Uses installed {(installed.Length > 0 ? string.Join(" + ", installed) : mod.Name)}: {Formatting.Value(mod.Format, input.Value)}";
        });
        return new(output.Name, Formatting.Value(output.Format, output.Value), [.. details, .. output.Formula.Length > 0 ? [$"Formula: {output.Formula}"] : Array.Empty<string>()]);
    }
}

/// <summary>A skill's mod slot: which mod, and its roll (typed, or set to its maximum).</summary>
public sealed class SkillModViewModel : RollViewModel
{
    private readonly string slotId;
    private readonly string skillId;
    private readonly ActiveSkillModSlotDefinition definition;
    private Choice selected = Choice.None;

    /// <summary>The mod chosen in this slot, if any.</summary>
    internal ActiveSkillModDefinition? Installed => definition.Choices.FirstOrDefault(choice => choice.ID == selected.Id);

    internal SkillModViewModel(EditorViewModel editor, string slotId, string skillId, ActiveSkillModSlotDefinition definition) : base(editor)
    {
        (this.slotId, this.skillId, this.definition) = (slotId, skillId, definition);
        // Disabled until the resolution says the loadout accepts the skill.
        IsEnabled = false;
        (Id, Name) = (definition.ID, definition.Name);
        Choices = [Choice.None, .. definition.Choices.Select(choice => new Choice(choice.ID, Label(choice, definition.Choices)))];
    }

    /// <summary>The mod slot's id within its skill.</summary>
    public string Id { get; }
    public string Name { get; }
    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    /// <summary>Whether the loadout accepts the skill, so its mods can be changed.</summary>
    internal void Accept(bool accepted) => IsEnabled = accepted;


    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || Editor.Refreshing) { return; }
            // A new mod starts at its highest roll.
            Editor.Apply(value.Id.Length == 0 ? SkillModOperation.Cleared(slotId, definition.ID) : new SkillModOperation(slotId, definition.ID, value.Id));
        }
    }

    protected override BuildOperation Operation(double value) => new SkillModOperation(slotId, definition.ID, selected.Id, value);

    protected override string SlideKey => $"skill mod {slotId} {definition.ID}";

    protected override BuildOperation? Highest() => selected.Id.Length > 0 ? new SkillModOperation(slotId, definition.ID, selected.Id) : null;

    public override string RollLabel => Name;

    internal void Refresh(BuildSnapshot build)
    {
        var selection = build.SkillMods.GetValueOrDefault(slotId);
        var roll = selection is not null && selection.SkillId == skillId ? selection.Mods.GetValueOrDefault(definition.ID) : null;
        Selected = Listed(Choices, roll?.ModId ?? "", list => Choices = list);
        var mod = roll is null ? null : definition.Choices.FirstOrDefault(entry => entry.ID == roll.ModId);
        ShowRoll(mod is null ? null : new AttributeRollBounds(mod.Min, mod.Max, mod.Step), mod?.Format ?? StatFormats.Decimal, roll?.Value);
        MaximumCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A mod by what it does, "Name · +2.5–10% Health", with its id when another choice reads the same (mods are chosen by effect).</summary>
    internal static string Label(ActiveSkillModDefinition choice, ImmutableArray<ActiveSkillModDefinition> all)
    {
        var label = $"{choice.Name} · {Effect(choice)}";
        return all.Any(other => other.ID != choice.ID && other.Name == choice.Name && Effect(other) == Effect(choice)) ? $"{label} ({choice.ID})" : label;
    }

    private static string Effect(ActiveSkillModDefinition choice)
    {
        var percent = choice.Format == StatFormats.Percent;
        string Number(double value) => (percent ? value * 100 : value).ToString("0.##########", CultureInfo.CurrentCulture);
        var roll = choice.Max != choice.Min ? $"{Number(choice.Min)}–{Number(choice.Max)}" : Number(choice.Min);
        return $"+{roll}{(percent ? "%" : "")} {choice.StatName}".Trim();
    }
}
