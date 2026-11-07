using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// One stat as a summary row, as the Go app's: a short label, its value in gold, and its breakdown on request. A
/// conditional row shows only while its value differs from an empty build's, so it appears once something feeds it.
/// </summary>
public sealed class StatRowViewModel(StatDefinition definition, bool conditional, string label = "", double baseline = 0,
    Action<StatRowViewModel>? select = null) : ViewModelBase
{
    private string value = "–";
    private bool visible = !conditional;
    private int change;

    public string Id { get; } = definition.ID;
    public string Name { get; } = definition.Name;
    /// <summary>The name as the summary shows it ("Reload-Cycle Bodyshot DPS" for "Estimated Reload-Cycle Bodyshot DPS").</summary>
    public string Label { get; } = label.Length > 0 ? label : definition.Name;
    public string Format { get; } = definition.Format;
    public string Value { get => value; private set => SetProperty(ref this.value, value); }
    public bool IsVisible { get => visible; private set => SetProperty(ref visible, value); }
    /// <summary>Asks for the breakdown (the row's info button).</summary>
    public IRelayCommand SelectCommand => field ??= new RelayCommand(() => select?.Invoke(this));
    /// <summary>How the value was worked out, once asked for.</summary>
    public StatDetailsViewModel? Details { get; internal set => SetProperty(ref field, value); }

    /// <summary>While previewing, whether the previewed value is up (1) or down (-1) on the build's; 0 otherwise.</summary>
    public int Change { get => change; private set { if (SetProperty(ref change, value)) { OnPropertyChanged(nameof(IsUp)); OnPropertyChanged(nameof(IsDown)); } } }
    public bool IsUp => Change > 0;
    public bool IsDown => Change < 0;

    /// <summary>Shows the build's value, or while previewing (Go's picker highlight), the candidate's with its rise or fall marked.</summary>
    internal void Show(ImmutableDictionary<string, double> stats, ImmutableDictionary<string, double>? preview = null)
    {
        var known = stats.TryGetValue(Id, out var number);
        IsVisible = !conditional || (known && HeadlineViewModel.Applies(number, baseline));
        if (preview is null) { (Value, Change) = (known ? Formatting.Value(Format, number) : "–", 0); return; }
        var shown = preview.GetValueOrDefault(Id);
        IsVisible = IsVisible || HeadlineViewModel.Applies(shown, baseline);
        Change = Formatting.Direction(shown, number);
        Value = Formatting.Value(Format, shown) + Formatting.Mark(Change);
    }

    /// <summary>
    /// Each row's label, as Go's summaryRowLabels: without "Estimated ", "Combined ", "Effective " and "Equipped " and, in
    /// the Weapon group, a leading "Weapon "; a label that would read the same as another row's keeps the full name.
    /// </summary>
    public static ImmutableDictionary<string, string> Labels(IEnumerable<LayoutSummaryGroup> groups, IReadOnlyDictionary<string, StatDefinition> stats)
    {
        string Short(string group, StatDefinition stat)
        {
            var label = stat.Name;
            foreach (var prefix in new[] { "Estimated ", "Combined ", "Effective ", "Equipped " })
            { if (label.StartsWith(prefix, StringComparison.Ordinal)) { label = label[prefix.Length..]; } }
            if (group == "Weapon" && label.StartsWith("Weapon ", StringComparison.Ordinal) && label.Length > "Weapon ".Length) { label = label["Weapon ".Length..]; }
            return label.Length == 0 ? stat.Name : label;
        }
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // A stat listed twice (in a group's core and conditional lists, or in two groups) is one row's label.
            foreach (var id in group.Stats.Concat(group.Conditional).Where(stats.ContainsKey).Where(id => !labels.ContainsKey(id)))
            {
                var label = labels[id] = Short(group.Name, stats[id]);
                uses[label] = uses.GetValueOrDefault(label) + 1;
                // A full name another row keeps also counts.
                if (label != stats[id].Name) { uses[stats[id].Name] = uses.GetValueOrDefault(stats[id].Name) + 1; }
            }
        }
        return labels.ToImmutableDictionary(pair => pair.Key, pair => pair.Value != stats[pair.Key].Name && uses[pair.Value] > 1 ? stats[pair.Key].Name : pair.Value, StringComparer.Ordinal);
    }
}

/// <summary>A summary group of stats, as the pack's layout names them; its header hides while none of its rows show.</summary>
public sealed class StatGroupViewModel(string name, ImmutableArray<StatRowViewModel> rows) : ViewModelBase
{
    private bool visible = true;

    public string Name { get; } = name;
    public ImmutableArray<StatRowViewModel> Rows { get; } = rows;
    public bool HasVisible { get => visible; private set => SetProperty(ref visible, value); }

    internal void Update() => HasVisible = Rows.Any(row => row.IsVisible);
}

/// <summary>One weapon slot's tab in the summary ("Primary"): choosing it puts that weapon in hand, choosing it again clears it.</summary>
public sealed class WeaponTabViewModel(string slotId, string label, Action<string> toggle) : ViewModelBase
{

    public string SlotId { get; } = slotId;
    public string Label { get; } = label;
    public bool IsActive { get; internal set => SetProperty(ref field, value); }
    public CommunityToolkit.Mvvm.Input.IRelayCommand ToggleCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => toggle(slotId));
}

/// <summary>An attribute category's configured core and other attributes on the counted gear, as the Go summary's grid.</summary>
public sealed class AttributeCountViewModel(string name, string color) : ViewModelBase
{
    private int core;
    private int other;

    public string Name { get; } = name;
    public string Color { get; } = color;
    public int Core { get => core; internal set => SetProperty(ref core, value); }
    public int Other { get => other; internal set => SetProperty(ref other, value); }
}

/// <summary>A headline figure, as the Go app's boxes: a caption, a value (or a status in its place) and a detail line.</summary>
public abstract class FigureViewModel : ViewModelBase
{
    private string label = "";
    private string value = "–";
    private string detail = "";
    private bool visible = true;
    private int change;

    public string Label { get => label; protected set => SetProperty(ref label, value); }
    public string Value { get => value; protected set => SetProperty(ref this.value, value); }
    /// <summary>A line under the value saying what it is or why there is none, or "".</summary>
    public string Detail { get => detail; protected set { if (SetProperty(ref detail, value)) { OnPropertyChanged(nameof(HasDetail)); } } }
    public bool HasDetail => Detail.Length > 0;
    public bool IsVisible { get => visible; protected set => SetProperty(ref visible, value); }
    /// <summary>The value is a status ("Unavailable", "Not modeled"), shown smaller than a number.</summary>
    public bool IsMessage { get; protected set => SetProperty(ref field, value); }

    /// <summary>While previewing, whether the figure got better (1) or worse (-1), for its colour; 0 otherwise.</summary>
    public int Change { get => change; protected set { if (SetProperty(ref change, value)) { OnPropertyChanged(nameof(IsUp)); OnPropertyChanged(nameof(IsDown)); } } }
    public bool IsUp => Change > 0;
    public bool IsDown => Change < 0;

    /// <summary>
    /// Shows the figure from a resolution, or with null (the build couldn't be resolved) none. With a preview (Go's
    /// picker highlight), it shows the candidate's figure, its rise or fall marked against the build's.
    /// </summary>
    internal abstract void Show(ResolvedBuild? resolved, ResolvedBuild? preview = null);

    /// <summary>Marks a previewed value against the build's; lower-is-better reverses the colour, not the arrow.</summary>
    protected void Marked(double shown, double current, bool lowerIsBetter = false)
    {
        var direction = Formatting.Direction(shown, current);
        Value += Formatting.Mark(direction);
        Change = lowerIsBetter ? -direction : direction;
    }
}

/// <summary>
/// A headline stat or build output, as the Go app's (G363): a stat with an unavailable reason stays shown, reading
/// "Unavailable" with the reason while it isn't above 0; a build output hides while it isn't above 0 and has its
/// description below; a conditional stat hides while it equals an empty build's; others always show.
/// </summary>
public sealed class HeadlineViewModel : FigureViewModel
{
    private readonly LayoutHeadlineStat entry;
    private readonly string format;
    private readonly string description;
    private readonly double? baseline;

    internal HeadlineViewModel(LayoutHeadlineStat entry, string label, string format, string description, double? baseline)
    {
        (this.entry, this.format, this.description, this.baseline) = (entry, format, description, baseline);
        Label = label;
    }

    /// <summary>A fall is the better way, as for a time to kill.</summary>
    public bool LowerIsBetter => entry.LowerIsBetter;

    private double? Number(ResolvedBuild? resolved) => resolved is null ? null
        : entry.Stat.Length > 0 ? resolved.Calculation.Stats.TryGetValue(entry.Stat, out var stat) ? stat : null
        : resolved.BuildOutputs.FirstOrDefault(output => output.Id == entry.BuildOutput)?.Value;

    internal override void Show(ResolvedBuild? resolved, ResolvedBuild? preview = null)
    {
        var current = Number(resolved);
        var number = preview is null ? current : Number(preview);
        (IsMessage, Change) = (false, 0);
        if (entry.BuildOutput.Length > 0)
        {
            IsVisible = number is > 0;
            Value = number is { } output ? Formatting.Headline(format, output) : "–";
            Detail = description;
            if (preview is not null && number is > 0 && current is > 0) { Marked(number.Value, current.Value, LowerIsBetter); }
        }
        else if (entry.Unavailable.Length > 0)
        {
            IsVisible = true;
            if (number is > 0)
            {
                (Value, Detail) = (Formatting.Headline(format, number.Value), "");
                if (preview is not null && current is > 0) { Marked(number.Value, current.Value, LowerIsBetter); }
            }
            else { (Value, Detail, IsMessage) = ("Unavailable", entry.Unavailable, true); }
        }
        else
        {
            IsVisible = !entry.Conditional || (current is { } value && Applies(value, baseline ?? 0))
                || (preview is not null && number is { } previewed && Applies(previewed, baseline ?? 0));
            Value = number is { } shown ? Formatting.Headline(format, shown) : "–";
            if (preview is not null && number is not null && current is not null) { Marked(number.Value, current.Value, LowerIsBetter); }
        }
    }

    /// <summary>Whether a value differs from the empty build's, as Go's summaryApplies.</summary>
    internal static bool Applies(double value, double baseline) => Math.Abs(value - baseline) > 1e-9 * Math.Max(1, Math.Abs(baseline));
}

/// <summary>
/// One skill slot's headline tile, as the Go app's: the accepted skill's headline rate (the pack marks one; rates are
/// never summed, as how bleeds, stings and ticks overlap is unknown), or for a shield its health and regeneration, or
/// why there is no figure. Hidden while the slot has no accepted skill.
/// </summary>
public sealed class SkillHeadlineViewModel(EditorViewModel editor, ActiveSkillSlotDefinition slot) : FigureViewModel
{
    public string SlotId => slot.ID;

    /// <summary>The skill's headline rate, or for a shield its health, as a number to mark a preview against.</summary>
    private double? Figure(ResolvedBuild? resolved)
    {
        if (resolved?.ActiveSkillPreviews.GetValueOrDefault(slot.ID) is not { } shown || editor.Skills.GetValueOrDefault(shown.SkillId) is not { } skill) { return null; }
        var headline = skill.PreviewOutputs.FirstOrDefault(output => output.Headline)?.ID;
        return (headline is null ? null : shown.Outputs.FirstOrDefault(output => output.Id == headline)?.Value)
            ?? shown.Outputs.FirstOrDefault(output => output.Kind == PreviewOutputKinds.Shield && output.Value > 0)?.Value;
    }

    internal override void Show(ResolvedBuild? resolved, ResolvedBuild? candidate = null)
    {
        Change = 0;
        var source = candidate ?? resolved;
        var preview = source?.ActiveSkillPreviews.GetValueOrDefault(slot.ID);
        if (preview is null || editor.Skills.GetValueOrDefault(preview.SkillId) is not { } skill) { IsVisible = false; return; }
        IsVisible = true;
        ShowSkill(skill, preview);
        // While previewing, the figure is marked against the build's when both have one, as Go's.
        if (candidate is not null && Figure(resolved) is { } current && Figure(candidate) is { } previewed) { Marked(previewed, current); }
    }

    private void ShowSkill(ActiveSkillDefinition skill, SkillPreview preview)
    {
        var headline = skill.PreviewOutputs.FirstOrDefault(output => output.Headline)?.ID;
        var rate = headline is null ? null : preview.Outputs.FirstOrDefault(output => output.Id == headline);
        var shield = preview.Outputs.FirstOrDefault(output => output.Kind == PreviewOutputKinds.Shield);
        var regeneration = preview.Outputs.FirstOrDefault(output => output.Kind == PreviewOutputKinds.Regeneration);
        Label = $"{slot.Name} DPS · {skill.Name}";
        if (rate is null && shield is { Value: > 0 })
        {
            Label = $"{slot.Name} · {skill.Name}";
            (Value, IsMessage) = (Formatting.Headline(StatFormats.Integer, shield.Value), false);
            // Health a second while the shield is up.
            Detail = regeneration is { Value: > 0 } ? $"Shield health · regenerates {Formatting.Value(StatFormats.Integer, regeneration.Value)}/s" : "Shield health";
        }
        else if (rate is not null) { (Value, Detail, IsMessage) = (Formatting.Headline(StatFormats.Decimal, rate.Value), rate.Name, false); }
        else if (skill.PreviewOutputs.Any(output => output.Kind == PreviewOutputKinds.Damage)) { (Value, Detail, IsMessage) = ("Not modeled", "Skill timing data pending", true); }
        else { (Value, Detail, IsMessage) = ("No damage estimate", "This skill has no modeled damage", true); }
    }
}

/// <summary>
/// How a stat came to its value, as the Go app's breakdown: its formula in names, the weapon slot for a weapon stat,
/// the base, each stat the formula reads, the bonuses with their sources ("+45 Source", "+12% Source"), and the final value.
/// </summary>
public sealed record StatDetailsViewModel(string Name, ImmutableArray<string> Lines);

/// <summary>
/// One line of the diagnostics strip, as the Go app's: each error on its own ("Error: …"), and warnings of one kind
/// collapsed into one ("Warning: … (and 2 more like this)").
/// </summary>
public sealed record DiagnosticLine(string Text, bool IsError)
{
    public static ImmutableArray<DiagnosticLine> Of(IEnumerable<BuildDiagnostic> diagnostics)
    {
        var all = diagnostics.ToList();
        var errors = all.Where(diagnostic => diagnostic.Severity == "error").Select(diagnostic => new DiagnosticLine($"Error: {diagnostic.Message}", true));
        var warnings = all.Where(diagnostic => diagnostic.Severity == "warning").GroupBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Select(group => new DiagnosticLine(group.Count() == 1 ? $"Warning: {group.First().Message}"
                : $"Warning: {group.First().Message} (and {group.Count() - 1} more like this)", false));
        return [.. errors, .. warnings];
    }
}
