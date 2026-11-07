using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The editor's window, headless: its controls are bound, and using them edits the build.</summary>
public sealed class EditorViewTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "editor-view-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;

    public EditorViewTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        // With the pack's artwork, as the app has it, so tiles and snapshots show it.
        var artwork = new PackArtwork(TestPack.Division.Pack.Presentation, Path.Combine(AppContext.BaseDirectory, "packs", "division-2"));
        editor = new(session, new FileBuildLibrary(folder), dispatcher, artwork);
    }

    public void Dispose()
    {
        editor.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private Window Show()
    {
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, Func<T, bool> match) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(match);

    /// <summary>Clicks a loadout tile, so the inspector shows what it holds.</summary>
    private static void Inspect(Window window, Func<object, bool> target)
    {
        var tile = Find<Button>(window, button => button.DataContext is TileViewModel tile && target(tile.Target));
        tile.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void InspectSlot(Window window, string slotId) => Inspect(window, target => target is SlotViewModel slot && slot.Id == slotId);

    /// <summary>Inspects a slot and equips an item in it, as Equip in the picker does.</summary>
    private SlotViewModel Equip(Window window, string slotId, string itemId)
    {
        InspectSlot(window, slotId);
        var slot = editor.Slot(slotId);
        slot.Equip(itemId);
        Dispatcher.UIThread.RunJobs();
        return slot;
    }

    [AvaloniaFact]
    public async Task PickingAnItemInTheWindowEditsTheBuild()
    {
        var window = Show();
        try
        {
            var maskSlot = Equip(window, "mask", "mask_n_5_11_tactical_mask");
            Assert.Equal("mask_n_5_11_tactical_mask", session.Snapshot.State.Build.Equipment["mask"]);
            // The inspector heads the slot with its item, and offers to change it.
            Assert.Equal("5.11 Tactical Mask", Find<TextBlock>(window, text => text.Name == "InspectorTitle").Text);
            Assert.Equal("Change item", Find<Button>(window, button => button.Name == "ChooseItem").Content);
            Assert.True(Find<Button>(window, button => button.Name == "UndoButton").IsEffectivelyEnabled);

            // The tile shows the item, and the inspector its attribute pickers; a typed roll commits when focus leaves its box.
            Assert.Equal("5.11 Tactical Mask", Find<Button>(window, button => button.DataContext is TileViewModel { Caption: "Mask" }).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("tiletitle")).Text);
            var core = maskSlot;
            core.Attributes.Single(attribute => attribute.Id == "core_1").Selected = core.Attributes.Single(attribute => attribute.Id == "core_1").Choices.Single(choice => choice.Id == "armor_gear_core");
            Dispatcher.UIThread.RunJobs();
            var box = Find<TextBox>(window, text => text.DataContext is AttributeViewModel { Id: "core_1" } && text.IsVisible);
            box.Focus();
            box.Text = "100000";
            Find<TextBox>(window, text => text.Name == "BuildNameBox").Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100000, session.Snapshot.State.Build.AttributeRolls["mask"]["core_1"].Value);
            await editor.Resolution;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            SaveSnapshot(window, "inspector.png");
            // The inspector's Rank Upgrades opens the ranking for this slot.
            var rank = Find<Button>(window, button => button.Name == "RankUpgradesButton");
            rank.Command!.Execute(rank.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.RankUpgrades);
            editor.CloseRankUpgrades();
            Dispatcher.UIThread.RunJobs();

            // A stat row shows how it's worked out.
            await editor.Resolution;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            var row = Find<Button>(window, button => button.DataContext is StatRowViewModel { Id: "character_armor" });
            row.Command!.Execute(row.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("character_armor", editor.SelectedStat?.Id);
            Assert.NotNull(((StatRowViewModel)row.DataContext!).Details);

            // Unavailable figures say why: no weapon is in hand yet.
            var practical = editor.Headline.Single(figure => figure.Label == "Practical Reload-Cycle DPS");
            Assert.Equal(("Unavailable", "Needs an equipped active weapon"), (practical.Value, practical.Detail));

            // A weapon equipped and put in hand through the window's pickers.
            Equip(window, "primary_weapon", "primary_weapon_acr");
            // The summary's Primary tab puts it in hand.
            Find<Button>(window, button => button.DataContext is WeaponTabViewModel { SlotId: "primary_weapon" }).Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("primary_weapon", session.Snapshot.State.Assumptions["weapon_slot"]);
            await editor.Resolution;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual("Unavailable", practical.Value);
            Assert.Equal("Primary Weapon · ACR", editor.ActiveWeaponText);

            // Prototype on the mask, a specialization node and a skill with a mod, for the snapshot.
            maskSlot.IsPrototype = true;
            maskSlot.Augment = maskSlot.Augments.Single(choice => choice.Id == "augment_echo");
            editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "gunner");
            editor.Specialization.Nodes[0].ToggleCommand.Execute(null);
            Inspect(window, target => target is SpecializationViewModel);
            Assert.True(Find<StackPanel>(window, panel => panel.Name == "SpecializationPanel").IsVisible);
            var turret = editor.SkillSlots[0];
            turret.Selected = turret.Choices.Single(choice => choice.Id == "skill_assault_turret");
            turret.Mods[0].Selected = turret.Mods[0].Choices[1];
            await editor.Resolution;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            // The skill's tile names it, and the inspector edits it.
            Inspect(window, target => target == turret);
            Assert.Contains(Find<ScrollViewer>(window, viewer => viewer.Name == "Inspector").GetVisualDescendants().OfType<ComboBox>(),
                box => box.SelectedItem is Choice { Id: "skill_assault_turret" });

            SaveSnapshot(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BrowsingOpensThePickerInTheEquipmentColumn()
    {
        var window = Show();
        try
        {
            InspectSlot(window, "chest");
            var browse = Find<Button>(window, button => button.Name == "ChooseItem");
            Assert.Equal("Choose item", browse.Content);
            browse.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Border>(window, border => border.Name == "ItemPicker").IsVisible);
            await editor.Picker!.Comparison;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();

            // Each row is tinted by its rarity's colour.
            var rows = Find<ListBox>(window, list => list.Name == "PickerRows");
            rows.SelectedItem = editor.Picker.Rows.Single(row => row.Id == "chest_n_5_11_tactical_chest");
            Dispatcher.UIThread.RunJobs();
            await editor.PreviewResolution;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            // While previewing, a summary value that rises is in the success colour, not the usual gold.
            var armorValue = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.DataContext is StatRowViewModel { Id: "character_armor" } && text.Classes.Contains("value"));
            Assert.Contains("up", armorValue.Classes);
            Assert.Equal(Avalonia.Media.Color.Parse("#4FC27A"), ((Avalonia.Media.ISolidColorBrush)armorValue.Foreground!).Color);
            var tint = rows.GetVisualDescendants().OfType<Border>().First(border => border.DataContext is ItemRowViewModel && border.Background is Avalonia.Media.ISolidColorBrush);
            Assert.Equal(Avalonia.Media.Color.Parse(((ItemRowViewModel)tint.DataContext!).Accent), ((Avalonia.Media.ISolidColorBrush)tint.Background!).Color);
            SaveSnapshot(window, "picker.png");

            Find<Button>(window, button => button.Name == "EquipButton").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("chest_n_5_11_tactical_chest", session.Snapshot.State.Build.Equipment["chest"]);
            Assert.False(Find<Border>(window, border => border.Name == "ItemPicker").IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>When CHARACTER_PLANNER_SNAPSHOT names a folder, saves one frame, for review.</summary>
    private static void SaveSnapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is not { Length: > 0 } directory) { return; }
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>When CHARACTER_PLANNER_SNAPSHOT names a folder, saves what the editor looks like, for review.</summary>
    private static void SaveSnapshot(Window window)
    {
        if (Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is not { Length: > 0 } directory) { return; }
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "editor.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        // The whole editor in one tall frame, so what is below the fold can be reviewed too.
        window.Height = 1800;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "editor-full.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
