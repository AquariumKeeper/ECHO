using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The specialization and its tree, as the Go app's panel (F21): the specialization, each budget's spent and total
/// points, and every node with its cost, prerequisites and state. A node is allocated only while its prerequisites are
/// active and its budgets have room; an allocated node whose prerequisite went is shown inactive, and can be removed.
/// </summary>
public sealed class SpecializationViewModel : ViewModelBase
{
    private readonly EditorViewModel editor;
    private SpecializationDefinition? shown;
    private Choice selected = Choice.None;
    private ImmutableArray<string> budgets = [];
    private ImmutableArray<SpecializationNodeViewModel> nodes = [];
    private string summary = "";

    internal SpecializationViewModel(EditorViewModel editor)
    {
        this.editor = editor;
        Tree = new SpecializationTreeViewModel(editor);
        var names = editor.Pack.Specializations.GroupBy(specialization => specialization.Name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        Choices = [Choice.None, .. editor.Pack.Specializations.Select(specialization => new Choice(specialization.ID,
            names.Contains(specialization.Name) ? $"{specialization.Name} ({specialization.ID})" : specialization.Name))];
    }

    private const string NoneSelected = "Select a specialization to plan its tree.";

    public ImmutableArray<Choice> Choices { get; private set => SetProperty(ref field, value); }

    public Choice Selected
    {
        get => selected;
        set
        {
            if (value is null || !SetProperty(ref selected, value) || value.IsUnknown || editor.Refreshing) { return; }
            editor.Apply(value.Id.Length == 0 ? SpecializationOperation.Cleared() : new SpecializationOperation(value.Id));
        }
    }

    public string Description { get; private set => SetProperty(ref field, value); } = NoneSelected;
    /// <summary>The selected specialization's name, for its heading, or "".</summary>
    public string Title => shown?.Name ?? "";
    public string? Icon { get; private set; }
    public string? Image { get; private set; }
    public bool HasImage => Image is not null;
    /// <summary>Each budget as "Name: spent / total points".</summary>
    public ImmutableArray<string> Budgets { get => budgets; private set => SetProperty(ref budgets, value); }
    public ImmutableArray<SpecializationNodeViewModel> Nodes { get => nodes; private set => SetProperty(ref nodes, value); }
    /// <summary>The same tree drawn as a graph, for its own window; it follows every change to the build.</summary>
    public SpecializationTreeViewModel Tree { get; }
    public bool HasTree => shown is not null;

    /// <summary>The specialization and its root budgets' points, for a heading ("Gunner · 45 / 165 points").</summary>
    public string Summary { get => summary; private set => SetProperty(ref summary, value); }

    internal void RefreshArtwork()
    {
        Icon = shown is null ? null : editor.Artwork.Icon("specializations", shown.ID);
        Image = shown is null ? null : editor.Artwork.SpecializationImage(shown.ID);
        foreach (var name in new[] { nameof(Icon), nameof(Image), nameof(HasImage) }) { OnPropertyChanged(name); }
        foreach (var node in Nodes) { node.RefreshArtwork(editor); }
        Tree.RefreshArtwork();
    }

    internal void Refresh(BuildSnapshot build)
    {
        var id = build.SpecializationId;
        var specialization = editor.Pack.Specializations.FirstOrDefault(entry => entry.ID == id);
        Selected = Listed(Choices, id, list => Choices = list);
        if (!ReferenceEquals(specialization, shown))
        {
            shown = specialization;
            Description = specialization?.Description ?? (id.Length == 0 ? NoneSelected : "");
            // Go's heading icon and the specialization's large image, where the pack has them.
            Icon = specialization is null ? null : editor.Artwork.Icon("specializations", specialization.ID);
            Image = specialization is null ? null : editor.Artwork.SpecializationImage(specialization.ID);
            foreach (var name in new[] { nameof(Icon), nameof(Image), nameof(HasImage), nameof(Title), nameof(HasTree) }) { OnPropertyChanged(name); }
            Nodes = specialization is null ? [] : [.. specialization.Nodes.Select(node => new SpecializationNodeViewModel(editor, specialization, node))];
        }
        Tree.Show(specialization, build);
        if (specialization is null)
        {
            Budgets = [];
            Summary = "";
            return;
        }
        var status = SpecializationRules.Status(specialization, build.SpecializationNodeSelections);
        Budgets = [.. specialization.Budgets.Select(budget => $"{budget.Name}: {Points(status.Spent.GetValueOrDefault(budget.ID))} / {Points(budget.Points)} points")];
        var roots = specialization.Budgets.Where(budget => budget.ParentID.Length == 0)
            .Select(budget => $"{Points(status.Spent.GetValueOrDefault(budget.ID))} / {Points(budget.Points)} points");
        Summary = string.Join(" · ", [specialization.Name, .. roots]);
        var allocated = build.SpecializationNodeSelections.ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var node in Nodes) { node.Show(allocated, status); }
    }

    private static string Points(long points) => points.ToString("#,0", CultureInfo.CurrentCulture);
}

/// <summary>One node of a specialization tree: its cost and prerequisites, whether it's allocated and active, and its toggle.</summary>
public sealed class SpecializationNodeViewModel : ViewModelBase
{
    private readonly SpecializationDefinition specialization;
    private readonly SpecializationNodeDefinition node;
    private bool allocated;
    private bool canToggle;
    private string status = "";

    internal SpecializationNodeViewModel(EditorViewModel editor, SpecializationDefinition specialization, SpecializationNodeDefinition node)
    {
        (this.specialization, this.node) = (specialization, node);
        Id = node.ID;
        Icon = editor.Artwork.Icon("nodes", node.ID);
        Name = node.Name;
        Description = node.Description + (node.Modifiers.IsEmpty
            ? "\nDescription only; no calculated stat effect."
            : "\nModeled in stat totals. Tier effects accumulate with earlier tiers.");
        ToggleCommand = new RelayCommand(() => editor.Apply(new SpecializationNodeOperation(node.ID, !allocated)), () => canToggle);
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>The node's icon file, or null.</summary>
    public string? Icon { get; private set => SetProperty(ref field, value); }
    internal void RefreshArtwork(EditorViewModel editor) => Icon = editor.Artwork.Icon("nodes", node.ID);
    /// <summary>Its cost, what it requires, and whether an allocated node is inactive.</summary>
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool IsAllocated { get => allocated; private set { if (SetProperty(ref allocated, value)) { OnPropertyChanged(nameof(ToggleText)); } } }
    public string ToggleText => IsAllocated ? "Deallocate" : "Allocate";
    /// <summary>Allocates the node, or deallocates it; allocating needs its prerequisites active and room in its budgets.</summary>
    public IRelayCommand ToggleCommand { get; }

    internal void Show(ImmutableHashSet<string> selected, SpecializationStatus state)
    {
        IsAllocated = selected.Contains(node.ID);
        var text = $"Cost: {node.Cost.ToString("#,0", CultureInfo.CurrentCulture)} points";
        if (!node.Prerequisites.IsEmpty)
        {
            var names = node.Prerequisites.Select(id => specialization.Nodes.FirstOrDefault(entry => entry.ID == id)?.Name ?? id);
            text += $"\nRequires: {string.Join(", ", names)}";
        }
        if (IsAllocated && !state.Active.Contains(node.ID)) { text += "\nInactive: missing prerequisite"; }
        Status = text;
        canToggle = IsAllocated || SpecializationRules.CanAllocate(specialization, node, state);
        ToggleCommand.NotifyCanExecuteChanged();
    }
}
