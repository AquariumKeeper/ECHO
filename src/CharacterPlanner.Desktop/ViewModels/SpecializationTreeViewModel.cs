using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A line of the tree between two boxes' centres, lit when any of the nodes it leads to is active.</summary>
public sealed record TreeEdge(Point From, Point To, bool Lit)
{
    /// <summary>The first tiers the line leads to: it's lit when any of them is allocated and active.</summary>
    internal ImmutableArray<string> Needs { get; init; } = [];
}

/// <summary>
/// The specialization as the game lays it out, approximately: a grid around the specialization in the middle, one card
/// per perk with a pip per tier. The signature weapon's perks sit to the upper left, the grenade above and the sidearm
/// below the middle, the armor kit perks straight up, the skill's perks in a row to the right, the tactical link's
/// perks to the lower right, and the weapon damage hub's perks around its box to the lower left. A budget inside a
/// perk's group (Technician's skill hub) gets its own box between the perk and its tiers. As a passive tree planner
/// does it, clicking a tier allocates the path to it (its missing prerequisites with it, in one step) when its budgets
/// have room, and clicking an allocated tier removes it and what needs it. Hovering or focusing a tier previews that
/// path and what it costs.
/// </summary>
public sealed class SpecializationTreeViewModel : ViewModelBase
{
    internal const double CellWidth = 224;
    internal const double CellHeight = 132;
    internal const double CardWidth = 200;
    internal const double CardHeight = 112;
    private const double Margin = 24;
    private const int Columns = 7;
    private const int Rows = 6;

    private readonly EditorViewModel editor;
    private SpecializationDefinition? specialization;
    private ImmutableArray<string> allocated = [];
    private SpecializationStatus status = new(ImmutableDictionary<string, long>.Empty, []);
    private TreeNodeViewModel? previewed;
    private string search = "";
    private double zoom = 0.85;

    internal SpecializationTreeViewModel(EditorViewModel editor) => this.editor = editor;

    public string Title => specialization is null ? "Specialization tree" : $"{specialization.Name} tree";
    public bool HasSpecialization => specialization is not null;
    /// <summary>Every tier, for allocating and searching; the cards hold them.</summary>
    public ImmutableArray<TreeNodeViewModel> Nodes { get; private set => SetProperty(ref field, value); } = [];
    public ImmutableArray<TreeCardViewModel> Cards { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>The boxes of budgets inside the total (the weapon damage hub), each over the perks it holds.</summary>
    public ImmutableArray<TreeBudgetViewModel> BudgetBoxes { get; private set => SetProperty(ref field, value); } = [];
    public ImmutableArray<TreeEdge> Edges { get; private set => SetProperty(ref field, value); } = [];
    public double Width { get; private set => SetProperty(ref field, value); }
    public double Height { get; private set => SetProperty(ref field, value); }
    /// <summary>The specialization's box in the middle: its top-left corner and size.</summary>
    public double HubLeft { get; private set => SetProperty(ref field, value); }
    public double HubTop { get; private set => SetProperty(ref field, value); }
    public double HubWidth => 2 * CellWidth - (CellWidth - CardWidth);
    public double HubHeight => 2 * CellHeight - (CellHeight - CardHeight);
    /// <summary>The specialization's name, for the hub.</summary>
    public string HubTitle => specialization?.Name ?? "";
    /// <summary>The hub's art: the specialization's image, else its icon, else null (the app's symbol stands in).</summary>
    public string? HubArt { get; private set => SetProperty(ref field, value); }
    /// <summary>The root budget's points, for the hub ("45 / 165").</summary>
    public string HubPoints { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>Each budget's spent and total points, as the inspector lists them.</summary>
    public ImmutableArray<string> Budgets { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>What clicking the hovered or focused tier would do, or how to use the tree.</summary>
    public string Status { get; private set => SetProperty(ref field, value); } = Hint;
    /// <summary>The last click's refusal ("Not enough points…"), or "".</summary>
    public string Refusal { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasRefusal)); } } } = "";
    public bool HasRefusal => Refusal.Length > 0;

    private const string Hint = "Click a tier to allocate it with what it needs; click an allocated tier to remove it and what needs it.";

    /// <summary>Highlights the tiers whose name or description has every word typed.</summary>
    public string Search
    {
        get => search;
        set
        {
            if (SetProperty(ref search, value ?? "")) { RefreshMatches(); }
        }
    }

    private void RefreshMatches()
    {
        foreach (var node in Nodes) { node.IsMatch = search.Trim().Length > 0 && TextSearch.MatchesAllWords(node.Name + " " + node.Description, search); }
        foreach (var card in Cards) { card.Refresh(); }
        OnPropertyChanged(nameof(Matches));
    }

    /// <summary>How many tiers the search highlights ("3 matches"), or "".</summary>
    public string Matches => search.Trim().Length == 0 ? "" : Nodes.Count(node => node.IsMatch) switch
    {
        1 => "1 match",
        var count => $"{count.ToString(CultureInfo.CurrentCulture)} matches",
    };

    public double Zoom { get => zoom; set => SetProperty(ref zoom, Math.Clamp(value, 0.25, 2.0)); }

    internal void Show(SpecializationDefinition? shown, BuildSnapshot build)
    {
        if (!ReferenceEquals(shown, specialization))
        {
            specialization = shown;
            previewed = null;
            Layout();
            RefreshArtwork();
            foreach (var name in new[] { nameof(Title), nameof(HasSpecialization), nameof(HubTitle) }) { OnPropertyChanged(name); }
            RefreshMatches();
            Refusal = "";
        }
        if (specialization is null)
        {
            allocated = [];
            Budgets = [];
            HubPoints = "";
            return;
        }
        allocated = build.SpecializationNodeSelections;
        status = SpecializationRules.Status(specialization, allocated);
        Budgets = [.. specialization.Budgets.Select(budget => $"{budget.Name}: {Points(status.Spent.GetValueOrDefault(budget.ID))} / {Points(budget.Points)} points")];
        HubPoints = string.Join(" · ", specialization.Budgets.Where(budget => budget.ParentID.Length == 0)
            .Select(budget => $"{Points(status.Spent.GetValueOrDefault(budget.ID))} / {Points(budget.Points)}"));
        foreach (var box in BudgetBoxes) { box.Points = $"{Points(status.Spent.GetValueOrDefault(box.Id))} / {Points(box.Total)}"; }
        var selected = allocated.ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            var isAllocated = selected.Contains(node.Id);
            node.ShowState(isAllocated, isAllocated && !status.Active.Contains(node.Id),
                !isAllocated && SpecializationRules.CanAllocate(specialization, node.Definition, status));
        }
        foreach (var card in Cards) { card.Refresh(); }
        Edges = [.. Edges.Select(edge => edge with { Lit = edge.Needs.Any(status.Active.Contains) })];
        Preview(previewed);
    }

    /// <summary>
    /// Takes the current artwork (the bundled pack's, or an installed artwork pack's over it): each card shows its
    /// perk's icon, bound in the "nodes" group to any of its tiers (the first tier's wins), and the hub the
    /// specialization's image or icon. Appearance only; the build is untouched.
    /// </summary>
    internal void RefreshArtwork()
    {
        var artwork = editor.Artwork;
        HubArt = specialization is null ? null : artwork.SpecializationImage(specialization.ID) ?? artwork.Icon("specializations", specialization.ID);
        foreach (var card in Cards) { card.RefreshArtwork(artwork); }
    }

    /// <summary>Shows what clicking a tier would do and marks its path; null clears the preview.</summary>
    public void Preview(TreeNodeViewModel? node)
    {
        previewed = node;
        var path = node is null || specialization is null ? [] : Effect(node).Nodes.Select(entry => entry.ID).ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var entry in Nodes) { entry.IsOnPath = path.Contains(entry.Id); }
        Status = node is null || specialization is null ? Hint : Effect(node).Text;
    }

    /// <summary>Allocates the path to a tier, or removes an allocated tier and what needs it, in one undoable step.</summary>
    public void Activate(TreeNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (specialization is null) { return; }
        var effect = Effect(node);
        if (!effect.Allowed)
        {
            Refusal = effect.Text;
            return;
        }
        Refusal = "";
        editor.Apply([.. effect.Nodes.Select(entry => (BuildOperation)new SpecializationNodeOperation(entry.ID, effect.Allocates))]);
    }

    private sealed record NodeEffect(ImmutableArray<SpecializationNodeDefinition> Nodes, bool Allocates, bool Allowed, string Text);

    private NodeEffect Effect(TreeNodeViewModel node)
    {
        if (node.IsAllocated)
        {
            var removed = SpecializationRules.Dependents(specialization!, node.Definition, allocated).Insert(0, node.Definition);
            var after = removed.Length - 1;
            var text = $"Click to remove {node.Name}" + (after == 0 ? "" : $" and {Count(after, "node")} after it") + $": frees {Points(removed.Sum(entry => entry.Cost))} points.";
            return new(removed, false, true, text);
        }
        var path = SpecializationRules.PathTo(specialization!, node.Definition, allocated);
        var cost = path.Sum(entry => entry.Cost);
        var before = path.Length - 1;
        if (!SpecializationRules.CanAllocatePath(specialization!, path, status))
        { return new(path, true, false, $"Not enough points for {node.Name}: it takes {Points(cost)} points" + (before == 0 ? "." : $" with {Count(before, "node")} before it.")); }
        return new(path, true, true, $"Click to allocate {node.Name}" + (before == 0 ? "" : $" and {Count(before, "node")} before it") + $": {Points(cost)} points.");
    }

    /// <summary>A perk's tiers ("Incessant" 1 to 3) as one card, and the perk it hangs off, if any.</summary>
    private sealed record Chain(int Index, ImmutableArray<SpecializationNodeDefinition> Tiers, int Parent)
    {
        public SpecializationNodeDefinition Head => Tiers[0];
    }

    /// <summary>
    /// Places each perk's card on the game's grid: seven columns and six rows, the specialization over the middle two
    /// of each. Perks are grouped by the root they hang off, and a group's shape says where it goes, as it does in
    /// all six of the game's trees: the free root is the signature weapon's, the lone roots are the grenade and then
    /// the sidearm, the straight chain is the skill's, and of the two that branch, the one with more branches is the
    /// tactical link's and the other the armor kit's. Whatever doesn't fit those shapes goes on rows below.
    /// </summary>
    private void Layout()
    {
        if (specialization is null)
        {
            Nodes = [];
            Cards = [];
            BudgetBoxes = [];
            Edges = [];
            Width = 0;
            Height = 0;
            return;
        }
        var byId = specialization.Nodes.GroupBy(node => node.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        SpecializationNodeDefinition? Parent(SpecializationNodeDefinition node) => node.Prerequisites.Where(byId.ContainsKey).Select(id => byId[id]).FirstOrDefault();
        var children = specialization.Nodes.Where(node => Parent(node) is not null).GroupBy(node => Parent(node)!.ID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        // Chains: a head (a root, or a node whose parent is another perk's) and the same-named tiers after it.
        var chainOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var tiers = new List<List<SpecializationNodeDefinition>>();
        foreach (var head in specialization.Nodes.Where(node => Parent(node) is not { } parent || ChainName(parent.Name) != ChainName(node.Name)))
        {
            if (chainOf.ContainsKey(head.ID)) { continue; }
            var chain = new List<SpecializationNodeDefinition>();
            for (var node = head; node is not null && !chainOf.ContainsKey(node.ID);)
            {
                chainOf[node.ID] = tiers.Count;
                chain.Add(node);
                node = children.GetValueOrDefault(node.ID)?.FirstOrDefault(child => ChainName(child.Name) == ChainName(head.Name) && !chainOf.ContainsKey(child.ID));
            }
            tiers.Add(chain);
        }
        foreach (var node in specialization.Nodes.Where(node => !chainOf.ContainsKey(node.ID)))
        {
            chainOf[node.ID] = tiers.Count;
            tiers.Add([node]);
        }
        var chains = tiers.Select((chain, index) => new Chain(index, [.. chain], Parent(chain[0]) is { } parent && chainOf[parent.ID] != index ? chainOf[parent.ID] : -1)).ToList();
        List<Chain> Branches(Chain chain) => [.. chains.Where(other => other.Parent == chain.Index)];

        // Budgets inside the total: a hub of roots gets its own box off the middle; one whose perks hang off another
        // perk (Technician's skill hub) gets a box between that perk and them.
        var hubs = specialization.Budgets.Where(budget => budget.ParentID.Length > 0)
            .Select(budget => (Budget: budget, Chains: chains.Where(chain => chain.Head.BudgetID == budget.ID).ToList()))
            .Where(hub => hub.Chains.Count > 0).ToList();
        var freeHub = hubs.FirstOrDefault(hub => hub.Chains.All(chain => chain.Parent < 0));
        var inHub = hubs.SelectMany(hub => hub.Chains).Select(chain => chain.Index).ToHashSet();
        var attached = hubs.Where(hub => hub.Budget != freeHub.Budget && hub.Chains.Select(chain => chain.Parent).Distinct().Count() == 1 && hub.Chains[0].Parent >= 0)
            .ToDictionary(hub => hub.Chains[0].Parent, hub => hub);

        var cells = new Dictionary<int, (double Column, double Row)>();
        var boxCells = new Dictionary<string, (double Column, double Row)>(StringComparer.Ordinal);
        var taken = new HashSet<(double, double)>();
        bool Put(Chain? chain, double column, double row)
        {
            if (chain is null || cells.ContainsKey(chain.Index) || !taken.Add((column, row))) { return false; }
            cells[chain.Index] = (column, row);
            return true;
        }
        // The middle is the specialization's.
        foreach (var cell in new[] { (2.0, 2.0), (3.0, 2.0), (2.0, 3.0), (3.0, 3.0) }) { taken.Add(cell); }

        if (freeHub.Budget is not null)
        {
            taken.Add((1, 4));
            boxCells[freeHub.Budget.ID] = (1, 4);
            (double, double)[] around = [(0, 3), (1, 3), (0, 4), (2, 4), (0, 5), (1, 5), (2, 5)];
            foreach (var (chain, cell) in freeHub.Chains.Zip(around)) { Put(chain, cell.Item1, cell.Item2); }
        }
        var groups = chains.Where(chain => chain.Parent < 0 && !inHub.Contains(chain.Index)).ToList();
        int Size(Chain chain) => 1 + Branches(chain).Sum(Size);
        bool Straight(Chain chain) => Branches(chain) is var kids && (kids.Count == 0 || (kids.Count == 1 && Straight(kids[0])));
        var signature = groups.FirstOrDefault(chain => chain.Tiers.All(tier => tier.Cost == 0));
        var lone = groups.Where(chain => chain != signature && Size(chain) == 1).ToList();
        var skill = groups.FirstOrDefault(chain => chain != signature && Size(chain) > 1 && Straight(chain));
        var branching = groups.Where(chain => chain != signature && chain != skill && Size(chain) > 1)
            .OrderByDescending(chain => Branches(chain).Count + (attached.ContainsKey(chain.Index) ? 1 : 0)).ToList();
        var tactical = branching.ElementAtOrDefault(0);
        var armor = branching.ElementAtOrDefault(1);

        if (signature is not null && Put(signature, 1, 2))
        {
            var kids = Branches(signature);
            Put(kids.ElementAtOrDefault(0), 1, 1);
            Put(kids.ElementAtOrDefault(1), 0, 2);
        }
        Put(lone.ElementAtOrDefault(0), 2, 1);
        Put(lone.ElementAtOrDefault(1), 3, 4);
        if (armor is not null && Put(armor, 3, 1))
        {
            var kids = Branches(armor);
            Put(kids.ElementAtOrDefault(0), 3, 0);
            Put(kids.ElementAtOrDefault(1), 4, 0);
        }
        for (var (chain, column) = (skill, 4); chain is not null && column < Columns && Put(chain, column, 2); column++)
        { chain = Branches(chain).FirstOrDefault(); }
        if (tactical is not null && Put(tactical, 4, 4))
        {
            var kids = Branches(tactical).Where(kid => !inHub.Contains(kid.Index)).ToList();
            if (attached.TryGetValue(tactical.Index, out var hub) && taken.Add((5, 4)))
            {
                boxCells[hub.Budget.ID] = (5, 4);
                Put(hub.Chains.ElementAtOrDefault(0), 5, 3);
                Put(hub.Chains.ElementAtOrDefault(1), 6, 4);
            }
            Put(kids.ElementAtOrDefault(0), 4, 5);
            Put(kids.ElementAtOrDefault(1), 5, 5);
            Put(kids.ElementAtOrDefault(2), 5, 4);
        }
        // Anything else, in pack order, on rows below the grid.
        var extra = 0;
        foreach (var chain in chains.Where(chain => !cells.ContainsKey(chain.Index)))
        {
            while (!Put(chain, extra % Columns, Rows + extra / Columns)) { extra++; }
            extra++;
        }
        foreach (var hub in hubs.Where(hub => !boxCells.ContainsKey(hub.Budget.ID)))
        {
            while (!taken.Add((extra % Columns, Rows + extra / Columns))) { extra++; }
            boxCells[hub.Budget.ID] = (extra % Columns, Rows + extra / Columns);
            extra++;
        }

        var rows = Math.Max(Rows, (int)taken.Max(cell => cell.Item2) + 1);
        Width = 2 * Margin + Columns * CellWidth;
        Height = 2 * Margin + rows * CellHeight;
        Point Center((double Column, double Row) cell) => new(Margin + (cell.Column + 0.5) * CellWidth, Margin + (cell.Row + 0.5) * CellHeight);
        var middle = Center((2.5, 2.5));
        HubLeft = middle.X - HubWidth / 2;
        HubTop = middle.Y - HubHeight / 2;

        var nodes = specialization.Nodes.ToDictionary(node => node.ID, node => new TreeNodeViewModel(this, node), StringComparer.Ordinal);
        Nodes = [.. specialization.Nodes.Select(node => nodes[node.ID])];
        Cards = [.. chains.Select(chain => new TreeCardViewModel(ChainName(chain.Head.Name), Center(cells[chain.Index]), [.. chain.Tiers.Select(tier => nodes[tier.ID])]))];
        BudgetBoxes = [.. hubs.Select(hub => new TreeBudgetViewModel(hub.Budget, Center(boxCells[hub.Budget.ID])))];

        var edges = ImmutableArray.CreateBuilder<TreeEdge>();
        void Line(Point from, Point to, IEnumerable<Chain> leadsTo) => edges.Add(new(from, to, false) { Needs = [.. leadsTo.Select(chain => chain.Head.ID)] });
        foreach (var hub in hubs)
        {
            var box = Center(boxCells[hub.Budget.ID]);
            var parent = hub.Chains[0].Parent;
            Line(attached.ContainsKey(parent) && attached[parent].Budget == hub.Budget ? Center(cells[parent]) : middle, box, hub.Chains);
            foreach (var chain in hub.Chains) { Line(box, Center(cells[chain.Index]), [chain]); }
        }
        foreach (var chain in chains.Where(chain => !inHub.Contains(chain.Index)))
        { Line(chain.Parent < 0 ? middle : Center(cells[chain.Parent]), Center(cells[chain.Index]), [chain]); }
        Edges = edges.ToImmutable();
        foreach (var name in new[] { nameof(HubWidth), nameof(HubHeight) }) { OnPropertyChanged(name); }
    }

    /// <summary>A perk's name, from its first tier's: "This is my Rifle — Tier 1" is "This is my Rifle".</summary>
    internal static string ChainName(string name)
    {
        var tier = name.LastIndexOf(" — Tier ", StringComparison.Ordinal);
        return tier > 0 ? name[..tier] : name;
    }

    internal static string Points(long points) => points.ToString("#,0", CultureInfo.CurrentCulture);
    private static string Count(int count, string noun) => $"{count.ToString(CultureInfo.CurrentCulture)} {noun}{(count == 1 ? "" : "s")}";
}

/// <summary>One perk on the tree: its name, its tiers as pips, and whether any are allocated, inactive or found.</summary>
public sealed class TreeCardViewModel : ViewModelBase
{
    internal TreeCardViewModel(string name, Point center, ImmutableArray<TreeNodeViewModel> tiers)
    {
        Name = name;
        Center = center;
        Tiers = tiers;
        var total = tiers.Sum(tier => tier.Definition.Cost);
        Cost = total == 0 ? "Free" : tiers.Length == 1 ? $"{SpecializationTreeViewModel.Points(total)} points"
            : $"{string.Join(" + ", tiers.Select(tier => SpecializationTreeViewModel.Points(tier.Definition.Cost)))} points";
    }

    public string Name { get; }
    internal Point Center { get; }
    public double Left => Center.X - SpecializationTreeViewModel.CardWidth / 2;
    public double Top => Center.Y - SpecializationTreeViewModel.CardHeight / 2;
    public ImmutableArray<TreeNodeViewModel> Tiers { get; }
    /// <summary>What its tiers cost ("3 + 3 + 6 points", "Free").</summary>
    public string Cost { get; }
    public bool IsAllocated { get; private set => SetProperty(ref field, value); }
    public bool IsInactive { get; private set => SetProperty(ref field, value); }
    public bool IsMatch { get; private set => SetProperty(ref field, value); }
    /// <summary>The perk's icon file from the artwork, or null (the app's node symbol stands in).</summary>
    public string? Icon { get; private set => SetProperty(ref field, value); }

    internal void RefreshArtwork(PackArtwork artwork) => Icon = Tiers.Select(tier => artwork.Icon("nodes", tier.Id)).FirstOrDefault(icon => icon is not null);

    internal void Refresh()
    {
        IsAllocated = Tiers.Any(tier => tier is { IsAllocated: true, IsInactive: false });
        IsInactive = Tiers.Any(tier => tier.IsInactive);
        IsMatch = Tiers.Any(tier => tier.IsMatch);
    }
}

/// <summary>A budget inside the total, drawn as a box over the perks it holds ("Weapon Damage Hub", "15 / 45").</summary>
public sealed class TreeBudgetViewModel : ViewModelBase
{
    internal TreeBudgetViewModel(SpecializationBudgetDefinition budget, Point center)
    {
        Id = budget.ID;
        Name = budget.Name;
        Total = budget.Points;
        Center = center;
    }

    internal string Id { get; }
    internal long Total { get; }
    internal Point Center { get; }
    public string Name { get; }
    public double Left => Center.X - SpecializationTreeViewModel.CardWidth / 2;
    public double Top => Center.Y - SpecializationTreeViewModel.CardHeight / 2;
    public string Points { get; internal set => SetProperty(ref field, value); } = "";
}

/// <summary>One tier of a perk, a pip on its card: its state, and whether it's on the previewed path or matches the search.</summary>
public sealed class TreeNodeViewModel : ViewModelBase
{
    private readonly SpecializationTreeViewModel tree;

    internal TreeNodeViewModel(SpecializationTreeViewModel tree, SpecializationNodeDefinition definition)
    {
        this.tree = tree;
        Definition = definition;
        Tier = definition.Tier.ToString(CultureInfo.CurrentCulture);
        ActivateCommand = new RelayCommand(() => tree.Activate(this));
    }

    internal SpecializationNodeDefinition Definition { get; }
    public string Id => Definition.ID;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    /// <summary>The tier, shown on its pip ("1", "2", …).</summary>
    public string Tier { get; }
    public IRelayCommand ActivateCommand { get; }

    public bool IsAllocated { get; private set => SetProperty(ref field, value); }
    /// <summary>Allocated, but a prerequisite isn't active, so it gives nothing.</summary>
    public bool IsInactive { get; private set => SetProperty(ref field, value); }
    /// <summary>Not allocated, and allocating it alone fits now.</summary>
    public bool IsAvailable { get; private set => SetProperty(ref field, value); }
    public bool IsOnPath { get; internal set => SetProperty(ref field, value); }
    public bool IsMatch { get; internal set => SetProperty(ref field, value); }

    public string ToolTip { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>What assistive technology reads: the tier and its state.</summary>
    public string AccessibleName { get; private set => SetProperty(ref field, value); } = "";

    internal void ShowState(bool allocated, bool inactive, bool available)
    {
        (IsAllocated, IsInactive, IsAvailable) = (allocated, inactive, available);
        var state = inactive ? "Allocated, inactive: a tier before it is missing" : allocated ? "Allocated" : available ? "Available" : "Locked";
        var modeled = Definition.Modifiers.IsEmpty ? "Description only; no calculated stat effect." : "Modeled in stat totals.";
        ToolTip = $"{Name}\n{Description}\nCost: {Definition.Cost.ToString("#,0", CultureInfo.CurrentCulture)} points · {state}\n{modeled}";
        AccessibleName = $"{Name}, {state}";
    }

    public void Preview() => tree.Preview(this);
    public void EndPreview() => tree.Preview(null);
}
