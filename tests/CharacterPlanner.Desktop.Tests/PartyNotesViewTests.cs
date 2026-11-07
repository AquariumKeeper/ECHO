using System.Text.Json;
using Avalonia.Controls;
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

/// <summary>The Party and Notes tabs and the Share and Import buttons in the window, and Go-era codes through the app's composition.</summary>
public sealed class PartyNotesViewTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "party-view-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;

    public PartyNotesViewTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private Window Show(string tab)
    {
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        Find<TabItem>(window, item => item.Name == tab).IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Control root, Func<T, bool> match) where T : Control => root.GetVisualDescendants().OfType<T>().First(match);

    [AvaloniaFact]
    public void ThePartyTabListsMembersAndTicksTheirBuffs()
    {
        var window = Show("PartyTab");
        try
        {
            Assert.True(Find<TextBlock>(window, text => text.Name == "PartyEmptyText").IsVisible);
            Assert.True(Find<Button>(window, button => button.Name == "AddByHandButton").IsEffectivelyEnabled);
            Assert.Null(editor.Party.AddFromCode("", ShareCode.Encode(PartyViewModelTests.Giver("Alex"))));
            Assert.Null(editor.Party.AddByHand("Sam", "1000"));
            Assert.Null(editor.Party.AddByHand("Kim", ""));
            Dispatcher.UIThread.RunJobs();
            Assert.False(Find<TextBlock>(window, text => text.Name == "PartyEmptyText").IsVisible);
            Assert.False(Find<Button>(window, button => button.Name == "AddFromCodeButton").IsEffectivelyEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == PartyMemberViewModel.FromCode);

            // Ticking the box applies the buff.
            var vanguard = Find<CheckBox>(window, box => box.Content is string label && label.StartsWith("Vanguard: +", StringComparison.Ordinal));
            vanguard.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(session.Snapshot.State.Build.Party[0].Effects[0].Active);

            // A buff chosen for a hand-entered member is added ticked.
            var add = Find<ComboBox>(window, box => box.DataContext is PartyMemberViewModel { Name: "Sam" } && box.PlaceholderText == PartyMemberViewModel.BuffPlaceholder);
            add.SelectedItem = ((PartyMemberViewModel)add.DataContext!).BuffChoices.Single(choice => choice.Name == "Perfect Vanguard");
            Dispatcher.UIThread.RunJobs();
            Assert.True(session.Snapshot.State.Build.Party[1].Effects.Single().Active);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == PartyEffectViewModel.Outranked && text.IsEffectivelyVisible);

            // The armor box commits through its draft.
            var armor = Find<TextBox>(window, box => box.DataContext is PartyArmorViewModel && box.IsEffectivelyVisible && box.Text == "1000");
            armor.Text = "2000";
            ((PartyArmorViewModel)armor.DataContext!).CommitValueCommand.Execute(null);
            Assert.Equal(2000, session.Snapshot.State.Build.Party[1].Armor);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheNotesBoxEditsTheNotes()
    {
        var window = Show("NotesTab");
        var waiting = new List<Action>();
        editor.Notes.Schedule = waiting.Add;
        try
        {
            var box = Find<TextBox>(window, text => text.Name == "NotesBox");
            Assert.Equal(NotesViewModel.Placeholder, box.PlaceholderText);
            box.Text = "Rotation";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", session.Snapshot.State.Build.Meta.Notes);
            foreach (var action in waiting.ToList()) { action(); }
            Assert.Equal("Rotation", session.Snapshot.State.Build.Meta.Notes);
            Assert.True(Find<Button>(window, button => button.Name == "ShareButton").IsEffectivelyVisible);
            Assert.True(Find<Button>(window, button => button.Name == "ImportButton").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>The app reads Go-era codes for party members and Import, as the CLI does.</summary>
    [AvaloniaFact]
    public async Task TheAppReadsGoEraShareCodes()
    {
        var model = new MainViewModel();
        try
        {
            await model.LoadAsync(Services.AppPaths.Pack, new FileBuildLibrary(folder), dispatcher);
            var app = model.Editor!;
            var code = GoCode();
            Assert.False(ShareCode.IsAppCode(code));
            Assert.Null(app.Party.AddFromCode("Old", code));
            Assert.Equal(PartyMemberViewModel.FromCode, app.Party.Members.Single().Source);
            var import = app.Sharing.Read(code, out var problem);
            Assert.True(import is not null, problem);
            Assert.Equal("division_2", import.Build.PackId);
        }
        finally { model.Dispose(); }
    }

    /// <summary>The Go-era code the frozen party-buffs batch adds a member from.</summary>
    private static string GoCode()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "baseline"))) { directory = directory.Parent; }
        var path = Path.Combine(directory!.FullName, "baseline", "fixtures", "builds", "party-buffs", "operations.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.EnumerateArray()
            .Single(operation => operation.GetProperty("kind").GetString() == OperationKinds.PartyMember && operation.TryGetProperty("text", out _))
            .GetProperty("text").GetString()!;
    }
}
