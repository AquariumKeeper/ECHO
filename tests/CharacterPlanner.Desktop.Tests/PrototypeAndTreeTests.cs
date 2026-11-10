using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>A Prototype item's purple card with circuit traces, and the specialization tree drawn as a graph.</summary>
public sealed class PrototypeAndTreeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tree-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public PrototypeAndTreeTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    /// <summary>When CHARACTER_PLANNER_SNAPSHOT names a folder, saves one frame, for review.</summary>
    private static void SaveSnapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is not { Length: > 0 } directory) { return; }
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private BuildSnapshot Build => session.Snapshot.State.Build;
    private TileViewModel Tile(string caption) => editor.TileGroups.SelectMany(group => group.Tiles).Single(tile => tile.Caption == caption);
    private SpecializationTreeViewModel Tree => editor.Specialization.Tree;
    private TreeNodeViewModel TreeNode(string id) => Tree.Nodes.Single(node => node.Id == id);

    /// <summary>A chest that can be Prototype, from the pack's own rules.</summary>
    private string PrototypeChest() => TestPack.Division.Pack.Items.First(item => item.Slot == "chest" && PrototypeRules.Eligible(TestPack.Division.Pack, item, "chest")).ID;

    private void Gunner()
    {
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "gunner");
        dispatcher.RunPending();
    }

    [Fact]
    public void APrototypeItemsCardTurnsPurpleAndBackToItsRarity()
    {
        var chest = PrototypeChest();
        editor.Apply(new EquipmentOperation("chest", chest));
        dispatcher.RunPending();
        var rarity = Tile("Chest").Accent;
        Assert.NotEqual(Committed, rarity);
        Assert.False(Tile("Chest").IsPrototype);

        editor.Apply(new PrototypeOperation("chest"));
        dispatcher.RunPending();
        Assert.True(Tile("Chest").IsPrototype);
        Assert.Equal(Committed, Tile("Chest").Accent);
        Assert.Contains("Prototype", Tile("Chest").Subtitle, StringComparison.Ordinal);
        var slot = editor.Slot("chest");
        Assert.True(((IItemSummary)slot).IsPrototype);
        Assert.Equal(Committed, slot.Accent);
        Assert.EndsWith(" · Prototype", slot.HeaderCaption, StringComparison.Ordinal);

        editor.Apply(PrototypeOperation.Cleared("chest"));
        dispatcher.RunPending();
        Assert.False(Tile("Chest").IsPrototype);
        Assert.Equal(rarity, Tile("Chest").Accent);
        Assert.Equal(rarity, slot.Accent);
        Assert.DoesNotContain("Prototype", slot.HeaderCaption, StringComparison.Ordinal);
    }

    // The committed pack's Prototype colour, from the game's loot quality table (R78).
    private const string Committed = "#AD78F1";

    [Fact]
    public void APackWithoutAPrototypeColourGetsTheAppsOwn()
    {
        // R74: with layout.prototype the tile and the header take the pack's colour; without one, the app's purple.
        Assert.Equal(Committed, PrototypeStyle.Color(TestPack.Division.Pack));
        var original = TestPack.Division.Pack;
        var rules = original with { Presentation = original.Presentation with { Layout = original.Presentation.Layout! with { Prototype = null } } };
        var pack = Assert.IsType<CharacterPlanner.Core.Packs.CompiledPackExpressions>(CharacterPlanner.Core.Packs.PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal(PrototypeStyle.Fallback, PrototypeStyle.Color(pack.Pack));
        var coloured = new PlannerSession(pack, PlannerState.Of(BuildSnapshot.Create(pack.Pack)), new OperationApplier(pack));
        using var other = new EditorViewModel(coloured, new FileBuildLibrary(folder), dispatcher);
        var chest = PrototypeChest();
        other.Apply(new EquipmentOperation("chest", chest));
        other.Apply(new PrototypeOperation("chest"));
        dispatcher.RunPending();
        Assert.Equal(PrototypeStyle.Fallback, other.TileGroups.SelectMany(group => group.Tiles).Single(tile => tile.Caption == "Chest").Accent);
        Assert.Equal(PrototypeStyle.Fallback, other.Slot("chest").Accent);
    }

    [Fact]
    public void CircuitTracesAreTheSeedsOwnAndStayOnTheCard()
    {
        var first = CircuitTraces.Traces("Chest", 300, 72, 0.6);
        Assert.Equal(first.SelectMany(trace => trace), CircuitTraces.Traces("Chest", 300, 72, 0.6).SelectMany(trace => trace));
        Assert.NotEqual(first.SelectMany(trace => trace), CircuitTraces.Traces("Mask", 300, 72, 0.6).SelectMany(trace => trace));
        Assert.InRange(first.Count, 2, 9);
        foreach (var point in first.SelectMany(trace => trace))
        {
            Assert.InRange(point.X, 300 * 0.4, 300);
            Assert.InRange(point.Y, 0, 72);
        }
        // Each trace runs in from the right edge, flat, then turns at 45°.
        foreach (var trace in first)
        {
            Assert.Equal(trace[0].Y, trace[1].Y);
            Assert.Equal(Math.Abs(trace[2].X - trace[1].X), Math.Abs(trace[2].Y - trace[1].Y), 6);
            Assert.Equal(trace[2].Y, trace[3].Y);
        }
    }

    /// <summary>A card's or box's cell on the grid: (column, row) from the top left.</summary>
    private static (double Column, double Row) Cell(Point center) =>
        (Math.Round((center.X - 24) / SpecializationTreeViewModel.CellWidth - 0.5, 2), Math.Round((center.Y - 24) / SpecializationTreeViewModel.CellHeight - 0.5, 2));

    private (double, double) CardCell(string name) => Cell(Tree.Cards.Single(card => card.Name == name).Center);

    private void Show(string id)
    {
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == id);
        dispatcher.RunPending();
    }

    [Fact]
    public void EveryPerkGetsACardOnTheGamesGridWithoutOverlaps()
    {
        Assert.False(Tree.HasSpecialization);
        foreach (var specialization in TestPack.Division.Pack.Specializations)
        {
            Show(specialization.ID);
            Assert.Equal($"{specialization.Name} tree", Tree.Title);
            Assert.Equal(specialization.Nodes.Length, Tree.Nodes.Length);
            // A card per perk, holding its tiers in order, and every tier on exactly one card.
            Assert.Equal(specialization.Nodes.Select(node => SpecializationTreeViewModel.ChainName(node.Name)).Distinct().Order(StringComparer.Ordinal),
                Tree.Cards.Select(card => card.Name).Order(StringComparer.Ordinal));
            Assert.Equal(specialization.Nodes.Select(node => node.ID).Order(StringComparer.Ordinal),
                Tree.Cards.SelectMany(card => card.Tiers).Select(node => node.Id).Order(StringComparer.Ordinal));
            foreach (var card in Tree.Cards)
            {
                Assert.All(card.Tiers, tier => Assert.Equal(card.Name, SpecializationTreeViewModel.ChainName(tier.Name)));
                Assert.Equal(Enumerable.Range(1, card.Tiers.Length).Select(tier => tier.ToString(CultureInfo.InvariantCulture)), card.Tiers.Select(tier => tier.Tier));
            }
            // A line ends at each card and each budget's box.
            Assert.Equal(Tree.Cards.Length + Tree.BudgetBoxes.Length, Tree.Edges.Length);
            // Everything fits the game's seven by six grid, inside the canvas, and nothing overlaps.
            Assert.Equal(2 * 24 + 6 * SpecializationTreeViewModel.CellHeight, Tree.Height);
            List<(string Name, double Left, double Top, double Width, double Height)> boxes = [.. Tree.Cards.Select(card => (card.Name, card.Left, card.Top, SpecializationTreeViewModel.CardWidth, SpecializationTreeViewModel.CardHeight))
                .Concat(Tree.BudgetBoxes.Select(box => (box.Name, box.Left, box.Top, SpecializationTreeViewModel.CardWidth, SpecializationTreeViewModel.CardHeight)))
                .Append(("middle", Tree.HubLeft, Tree.HubTop, Tree.HubWidth, Tree.HubHeight))];
            foreach (var box in boxes)
            {
                Assert.InRange(box.Left, 0, Tree.Width - box.Width);
                Assert.InRange(box.Top, 0, Tree.Height - box.Height);
                foreach (var other in boxes.Where(other => other != box))
                {
                    var apart = box.Left + box.Width <= other.Left || other.Left + other.Width <= box.Left || box.Top + box.Height <= other.Top || other.Top + other.Height <= box.Top;
                    Assert.True(apart, $"{box.Name} overlaps {other.Name} in {specialization.ID}");
                }
            }
            // As in the game: the signature weapon to the upper left, the weapon damage hub to the lower left.
            Assert.Equal((1, 2), CardCell("Signature Ammo Acquisition"));
            Assert.Equal((1, 1), CardCell("Signature Weapon Damage"));
            Assert.Equal((0, 2), CardCell("Group Signature Ammo Acquisition"));
            Assert.Equal((1, 4), Cell(Tree.BudgetBoxes.Single(box => box.Name == "Weapon Damage Hub").Center));
            Assert.Equal((0, 3), CardCell("This is my Rifle"));
            Assert.Equal((2, 4), CardCell("Gunslinger"));
            Assert.Equal((2, 5), CardCell("Spray and Pray"));
        }

        Gunner();
        // The grenade above the middle and the sidearm below it.
        Assert.Equal((2, 1), CardCell("Riot Foam Grenade"));
        Assert.Equal((3, 4), CardCell("P320 XCompact"));
        // The armor kit perks straight up.
        Assert.Equal((3, 1), CardCell("Incessant"));
        Assert.Equal((3, 0), CardCell("Coupler"));
        Assert.Equal((4, 0), CardCell("Hardened Armor Kits"));
        // The skill's perks in a row to the right.
        Assert.Equal((4, 2), CardCell("Banshee"));
        Assert.Equal((5, 2), CardCell("Microwave Amplifier"));
        Assert.Equal((6, 2), CardCell("Directional Transmitter"));
        // The tactical link's to the lower right: its first branch below, the second across, the third beside it.
        Assert.Equal((4, 4), CardCell("Supply Line"));
        Assert.Equal((4, 5), CardCell("Emplacement"));
        Assert.Equal((5, 5), CardCell("Vital Protection"));
        Assert.Equal((5, 4), CardCell("Barrage"));
        Assert.Equal(["5", "5", "5"], TreeCard("This is my Rifle").Cost.Split(" + ").Select(part => part.Replace(" points", "", StringComparison.Ordinal)));
        Assert.Equal("Free", TreeCard("Signature Ammo Acquisition").Cost);
        Assert.Equal("10 points", TreeCard("Supply Line").Cost);

        // Technician's skill hub is a box beside Faraday Field, over its two perks.
        Show("technician");
        Assert.Equal((4, 4), CardCell("Faraday Field"));
        var skillHub = Assert.Single(Tree.BudgetBoxes, box => box.Name != "Weapon Damage Hub");
        Assert.Equal((5, 4), Cell(skillHub.Center));
        Assert.Equal((5, 3), CardCell("Overclocked CPU"));
        Assert.Equal((6, 4), CardCell("Enhanced Diagnostics"));
        Assert.Equal((4, 5), CardCell("Technomancy"));
        Assert.Equal((5, 5), CardCell("Vital Protection"));
        Assert.Contains(Tree.Edges, edge => edge.From == Tree.Cards.Single(card => card.Name == "Faraday Field").Center && edge.To == skillHub.Center);

        // Demolitionist lists Vital Protection first, so it takes the place below the link.
        Show("demolitionist");
        Assert.Equal((4, 5), CardCell("Vital Protection"));
        Assert.Equal((5, 4), CardCell("Braced for Impact"));
    }

    private TreeCardViewModel TreeCard(string name) => Tree.Cards.Single(card => card.Name == name);

    [Fact]
    public void ClickingANodeAllocatesItsPathInOneStepAndRemovingTakesWhatNeedsIt()
    {
        Gunner();
        var third = TreeNode("gunner_this_is_my_rifle_tier_3");
        Assert.False(third.IsAvailable);
        third.Preview();
        Assert.Equal("Click to allocate This is my Rifle — Tier 3 and 2 nodes before it: 15 points.", Tree.Status);
        Assert.True(TreeNode("gunner_this_is_my_rifle_tier_1").IsOnPath);
        Assert.False(TreeNode("gunner_depleted_rounds_tier_1").IsOnPath);

        third.ActivateCommand.Execute(null);
        dispatcher.RunPending();
        Assert.Equal(["gunner_this_is_my_rifle_tier_1", "gunner_this_is_my_rifle_tier_2", "gunner_this_is_my_rifle_tier_3"], Build.SpecializationNodeSelections.Order(StringComparer.Ordinal));
        Assert.True(third.IsAllocated);
        Assert.Contains("Weapon Damage Hub: 15 / 45 points", Tree.Budgets);
        Assert.Equal("15 / 165", Tree.HubPoints);
        // The middle's line to the hub's box, and the box's to the perk.
        Assert.Equal(2, Tree.Edges.Count(edge => edge.Lit));
        Assert.True(TreeCard("This is my Rifle").IsAllocated);
        Assert.Contains("15 / 45", Tree.BudgetBoxes.Select(box => box.Points));

        // One step: a single undo takes the whole path back.
        editor.UndoCommand.Execute(null);
        dispatcher.RunPending();
        Assert.Empty(Build.SpecializationNodeSelections);
        editor.RedoCommand.Execute(null);
        dispatcher.RunPending();

        // Removing the first tier takes the two after it.
        var first = TreeNode("gunner_this_is_my_rifle_tier_1");
        first.Preview();
        Assert.Equal("Click to remove This is my Rifle — Tier 1 and 2 nodes after it: frees 15 points.", Tree.Status);
        first.ActivateCommand.Execute(null);
        dispatcher.RunPending();
        Assert.Empty(Build.SpecializationNodeSelections);
        first.EndPreview();
        Assert.DoesNotContain(Tree.Nodes, node => node.IsOnPath);
    }

    [Fact]
    public void APathTheBudgetCantHoldIsRefusedWithWhatItTakes()
    {
        Gunner();
        // The hub holds 45: three chains of three fill it.
        TreeNode("gunner_this_is_my_rifle_tier_3").ActivateCommand.Execute(null);
        dispatcher.RunPending();
        TreeNode("gunner_depleted_rounds_tier_3").ActivateCommand.Execute(null);
        dispatcher.RunPending();
        TreeNode("gunner_e_m_i_tier_3").ActivateCommand.Execute(null);
        dispatcher.RunPending();
        Assert.Equal(9, Build.SpecializationNodeSelections.Length);
        var blocked = Tree.Nodes.First(node => node.Definition.BudgetID == "weapon_damage_hub" && !node.IsAllocated && node.Definition.Prerequisites.IsEmpty);
        Assert.False(blocked.IsAvailable);
        blocked.ActivateCommand.Execute(null);
        dispatcher.RunPending();
        Assert.Equal(9, Build.SpecializationNodeSelections.Length);
        Assert.True(Tree.HasRefusal);
        Assert.StartsWith($"Not enough points for {blocked.Name}: it takes 5 points", Tree.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInactiveNodeShowsAndSearchHighlightsMatches()
    {
        Gunner();
        // The second tier alone (as an imported build may have it) is allocated but inactive.
        editor.Apply(new SpecializationNodeOperation("gunner_this_is_my_rifle_tier_2", true));
        dispatcher.RunPending();
        var second = TreeNode("gunner_this_is_my_rifle_tier_2");
        Assert.True(second.IsAllocated);
        Assert.True(second.IsInactive);
        Assert.Contains("inactive", second.AccessibleName, StringComparison.Ordinal);
        Assert.True(TreeCard("This is my Rifle").IsInactive);
        Assert.False(TreeCard("This is my Rifle").IsAllocated);
        Assert.DoesNotContain(Tree.Edges, edge => edge.Lit);

        Tree.Search = "rifle";
        Assert.All(Tree.Nodes.Where(node => node.IsMatch), node => Assert.Contains("rifle", node.Name + " " + node.Description, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Tree.Nodes, node => node.IsMatch);
        Assert.EndsWith("matches", Tree.Matches, StringComparison.Ordinal);
        Assert.True(TreeCard("This is my Rifle").IsMatch);
        Tree.Search = "";
        Assert.DoesNotContain(Tree.Nodes, node => node.IsMatch);
        Assert.Equal("", Tree.Matches);
    }

    [AvaloniaFact]
    public void TheTreeOpensFromTheInspectorAndAllocatesByClicking()
    {
        Gunner();
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        try
        {
            editor.Inspect(editor.Specialization);
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            var open = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenTreeButton");
            Assert.True(open.IsEffectivelyVisible);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var tree = Assert.IsType<SpecializationTreeWindow>(Assert.Single(window.OwnedWindows));
            Assert.Equal("Gunner tree", tree.Title);
            // Opening again brings the same window forward.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows);

            var buttons = tree.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("treenode")).ToList();
            Assert.Equal(Tree.Nodes.Length, buttons.Count);
            var rifle = buttons.Single(button => button.DataContext is TreeNodeViewModel { Id: "gunner_this_is_my_rifle_tier_2" });
            rifle.Command!.Execute(null);
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Build.SpecializationNodeSelections.Length);
            Assert.Contains("allocated", rifle.Classes);
            var hub = tree.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "TreeHub");
            Assert.Contains(hub.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "10 / 165");
            tree.Width = 1400;
            tree.Height = 1400;
            Dispatcher.UIThread.RunJobs();
            var before = Tree.Zoom;
            tree.Fit();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(before, Tree.Zoom);
            SaveSnapshot(tree, "specialization-tree.png");

            tree.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EveryCardHoldsItsPipsAndItsWholeName()
    {
        var window = new SpecializationTreeWindow { DataContext = Tree };
        window.Show();
        try
        {
            foreach (var specialization in TestPack.Division.Pack.Specializations)
            {
                Show(specialization.ID);
                Dispatcher.UIThread.RunJobs();
                var cards = window.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("treecard")).ToList();
                Assert.Equal(Tree.Cards.Length, cards.Count);
                foreach (var card in cards)
                {
                    var name = ((TreeCardViewModel)card.DataContext!).Name;
                    foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(control => control is Button || control is IconView))
                    {
                        var corner = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), card)!.Value;
                        Assert.True(corner.X <= card.Bounds.Width - 2 && corner.Y <= card.Bounds.Height - 2, $"{name} in {specialization.ID}: {control.GetType().Name} spills out");
                    }
                    var text = card.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == name);
                    Assert.False(text.TextLayout.TextLines.Any(line => line.HasCollapsed), $"{name} in {specialization.ID} is cut short");
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void APrototypeTileShowsItsCircuitTraces()
    {
        editor.Apply(new EquipmentOperation("chest", PrototypeChest()));
        dispatcher.RunPending();
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            CircuitTraces Circuit() => window.GetVisualDescendants().OfType<CircuitTraces>()
                .Single(control => control.Name == "TileCircuit" && control.DataContext is TileViewModel { Caption: "Chest" });
            Assert.False(Circuit().IsVisible);
            editor.Apply(new PrototypeOperation("chest"));
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Circuit().IsVisible);
            Assert.True(Circuit().Bounds.Width > 100);
            editor.Inspect(editor.Slot("chest"));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<CircuitTraces>(), control => control.Name == "HeaderCircuit" && control.IsEffectivelyVisible);
            SaveSnapshot(window, "prototype-card.png");
            editor.Inspect(editor.Slot("mask"));
            Dispatcher.UIThread.RunJobs();
            SaveSnapshot(window, "prototype-tile.png");
            Assert.Equal(Avalonia.Media.Color.Parse(Committed), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(Circuit().Stroke).Color);
        }
        finally { window.Close(); }
    }
}
