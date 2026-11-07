using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>
/// The editor's window driven by real input, headless: mouse clicks and keystrokes go through Avalonia's input
/// handling, as a person's would, and what assistive technology reads is checked alongside.
/// </summary>
public sealed class RealInputTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "real-input-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;

    public RealInputTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
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

    /// <summary>Clicks the middle of a control with the left mouse button.</summary>
    private static void Click(Window window, Control control)
    {
        var middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button Tile(Window window, string caption) => Find<Button>(window, button => button.DataContext is TileViewModel tile && tile.Caption == caption);

    [AvaloniaFact]
    public void ClickingATileSelectsItAndSaysSo()
    {
        var window = Show();
        try
        {
            var mask = Tile(window, "Mask");
            Assert.Equal("Mask: Empty", AutomationProperties.GetName(mask));
            Assert.Equal("", AutomationProperties.GetItemStatus(mask));

            Click(window, mask);
            Assert.True(((TileViewModel)mask.DataContext!).IsSelected);
            Assert.Equal("Selected", AutomationProperties.GetItemStatus(mask));
            // The inspector heads the slot at heading level 2.
            var title = Find<TextBlock>(window, text => text.Name == "InspectorTitle");
            Assert.Equal("Empty", title.Text);
            Assert.Equal(2, AutomationProperties.GetHeadingLevel(title));
            // An edit's refusal is announced at once.
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(Find<TextBlock>(window, text => text.Name == "MessageText")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TypingARollAndPressingEnterCommitsIt()
    {
        var window = Show();
        try
        {
            Click(window, Tile(window, "Mask"));
            var slot = editor.Slot("mask");
            slot.Equip("mask_n_5_11_tactical_mask");
            var core = slot.Attributes.Single(attribute => attribute.Id == "core_1");
            core.Selected = core.Choices.Single(choice => choice.Id == "armor_gear_core");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Mask: 5.11 Tactical Mask", AutomationProperties.GetName(Tile(window, "Mask")));

            var box = Find<TextBox>(window, text => text.DataContext == core && text.IsEffectivelyVisible);
            Click(window, box);
            box.SelectAll();
            window.KeyTextInput("100000");
            Dispatcher.UIThread.RunJobs();
            // Typing alone applies nothing; Enter does, and focus stays in the box.
            Assert.NotEqual(100000, session.Snapshot.State.Build.AttributeRolls["mask"]["core_1"].Value);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100000, session.Snapshot.State.Build.AttributeRolls["mask"]["core_1"].Value);
            Assert.True(box.IsFocused);
        }
        finally { window.Close(); }
    }
}
