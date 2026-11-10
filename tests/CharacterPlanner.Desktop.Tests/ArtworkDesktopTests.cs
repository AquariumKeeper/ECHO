using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Raw;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Artwork;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

public sealed partial class ArtworkDesktopTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4X8/wHwAGfQJ+unzLBwAAAABJRU5ErkJggg==");
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "artwork-ui-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "artwork");
        private readonly List<EditorViewModel> editors = [];
        public List<Window> Windows { get; } = [];
        public Fixture() => Directory.CreateDirectory(Root);
        public (EditorViewModel Editor, PlannerSession Session, ArtworkManagerViewModel Manager) Create(bool monitor = false)
        {
            var session = new PlannerSession(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
            var editor = new EditorViewModel(session, new FileBuildLibrary(Path.Combine(Root, "library")), AvaloniaDispatcher.Instance);
            var backend = new ArtworkPackInstaller(Store, new ArtworkPngDecoder());
            var manager = new ArtworkManagerViewModel(editor, PackArtwork.None, backend, backend, AvaloniaDispatcher.Instance, monitor ? Store : null);
            editor.ArtworkManager = manager;
            editors.Add(editor);
            return (editor, session, manager);
        }
        public string Zip(object? bindings = null, string version = "1")
        {
            var manifest = new
            {
                schemaVersion = 1,
                id = "example.desktop",
                name = "Orange slots",
                version,
                author = "Example author",
                gamePackId = "division_2",
                credits = new[] { new { author = "Example author", license = "MIT", licenseFile = "licenses/LICENSE.txt" } },
                assets = new[] { new { id = "mask", path = "assets/mask.png", sha256 = Convert.ToHexStringLower(SHA256.HashData(Png)) } },
                bindings = bindings ?? new { icons = new { slots = new { mask = "mask" } } }
            };
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, bytes) in new Dictionary<string, byte[]>
            {
                ["artwork.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest),
                ["assets/mask.png"] = Png,
                ["licenses/LICENSE.txt"] = Encoding.UTF8.GetBytes("MIT\nExample license notice")
            }) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
            return path;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var window in Windows) { window.Close(); }
            foreach (var editor in editors) { editor.Dispose(); }
            foreach (var editor in editors)
            { if (editor.ArtworkManager is { } manager) { await manager.DisposeAsync(); } }
            await Until(() => ArtworkBitmapCache.OwnersUnder(Root) == 0 &&
                (!Directory.Exists(Path.Combine(Store, ".leases")) || !Directory.EnumerateFiles(Path.Combine(Store, ".leases"), "*.lease", SearchOption.AllDirectories).Any()));
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > timeout) { Assert.Fail("Artwork UI did not reach the expected state"); }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task InstallPreviewUseRevertAndRemovePreserveTheEditorState()
    {
        await using var fixture = new Fixture();
        var (editor, session, manager) = fixture.Create();
        var item = TestPack.Division.Pack.Items.First(item => item.Slot == "mask" && !item.AttributeSlots.IsEmpty);
        Assert.True(editor.Slot("mask").Equip(item.ID));
        await editor.Resolution;
        await Until(() => editor.Slot("mask").IsEquipped);
        var draft = editor.Slot("mask").Attributes[0];
        draft.ValueText = "draft not committed";
        editor.BuildName = "Name being typed";
        editor.Browse(editor.Slot("mask"));
        await editor.Picker!.Comparison;
        var picker = editor.Picker;
        picker.Highlighted = picker.Rows[0];
        await editor.PreviewResolution;
        await Until(() => editor.Inspected is CandidateViewModel);
        var highlighted = picker.Highlighted;
        var previewResolution = editor.PreviewResolution;
        var editorWindow = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        fixture.Windows.Add(editorWindow);
        editorWindow.Show();
        var before = session.Snapshot;
        var document = BuildDocument.Encode(before.State.Build);
        var resolution = editor.Resolution;
        var simulate = editor.Simulate;
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip());
        await Until(() => manager.Previews.Length == 1 && manager.LicenseText.Contains("Example license", StringComparison.Ordinal));
        Assert.Null(editor.Artwork.Icon("slots", "mask"));
        Assert.Single(manager.Rows);
        Assert.True(manager.UseCommand.CanExecute(null));
        await manager.UseCommand.ExecuteAsync(null);
        Assert.Contains("Orange slots", manager.Current, StringComparison.Ordinal);
        Assert.NotNull(editor.Artwork.Icon("slots", "mask"));
        Assert.Equal(document, BuildDocument.Encode(session.Snapshot.State.Build));
        Assert.Equal(before.Revision, session.Snapshot.Revision);
        Assert.Same(resolution, editor.Resolution);
        Assert.Same(simulate, editor.Simulate);
        Assert.Same(picker, editor.Picker);
        Assert.Same(highlighted, picker.Highlighted);
        Assert.Same(previewResolution, editor.PreviewResolution);
        Assert.IsType<CandidateViewModel>(editor.Inspected);
        Assert.Equal("draft not committed", draft.ValueText);
        Assert.Equal("Name being typed", editor.BuildName);
        Assert.NotNull(editor.Slot("mask").Art.Image);
        await manager.BundledCommand.ExecuteAsync(null);
        Assert.Null(editor.Artwork.Icon("slots", "mask"));
        await manager.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(manager.Rows);
        Assert.Null(manager.Problem);
    }

    [AvaloniaFact]
    public async Task InvalidZipAndSettingsRecoveryStayInsideArtworkWorkflow()
    {
        await using var fixture = new Fixture();
        var (editor, session, manager) = fixture.Create();
        await manager.InitializeAsync();
        await manager.OpenAsync();
        var invalid = Path.Combine(fixture.Root, "invalid.zip");
        File.WriteAllText(invalid, "not a ZIP");
        await manager.InstallAsync(invalid);
        Assert.NotNull(manager.Problem);
        Assert.Empty(manager.Rows);
        Directory.CreateDirectory(fixture.Store);
        File.WriteAllText(Path.Combine(fixture.Store, "artwork-settings.json"), "{}");
        await manager.RefreshCommand.ExecuteAsync(null);
        Assert.True(manager.NeedsRecovery);
        Assert.NotNull(manager.SelectionProblem);
        await manager.RecoverCommand.ExecuteAsync(null);
        Assert.False(manager.NeedsRecovery);
        Assert.Null(manager.SelectionProblem);
        Assert.Single(Directory.EnumerateFiles(fixture.Store, ".settings-backup-*.json"));
        Assert.Equal(0, session.Snapshot.Revision);
        Assert.NotNull(editor.Simulate);
    }

    [AvaloniaFact]
    public async Task AnotherWindowsSelectionIsAppliedWithoutBuildEdits()
    {
        await using var fixture = new Fixture();
        var first = fixture.Create(monitor: true);
        var second = fixture.Create(monitor: true);
        await first.Manager.InitializeAsync();
        await second.Manager.InitializeAsync();
        await first.Manager.OpenAsync();
        await first.Manager.InstallAsync(fixture.Zip());
        await first.Manager.UseCommand.ExecuteAsync(null);
        await Until(() => second.Editor.Artwork.Icon("slots", "mask") is not null);
        Assert.Equal(0, first.Session.Snapshot.Revision);
        Assert.Equal(0, second.Session.Snapshot.Revision);
        await first.Manager.RemoveCommand.ExecuteAsync(null);
        await Until(() => second.Editor.Artwork.Icon("slots", "mask") is null);
        Assert.Empty(first.Manager.Rows);
        Assert.Null(first.Manager.Problem);
    }

    [AvaloniaTheory]
    [InlineData(900, 740, 1)]
    [InlineData(640, 520, 1)]
    [InlineData(900, 740, 1.5)]
    [InlineData(640, 520, 1.5)]
    [InlineData(900, 740, 2)]
    [InlineData(640, 520, 2)]
    public async Task ArtworkDialogHasCompiledBindingsPreviewsAndCredits(int width, int height, double scale)
    {
        await using var fixture = new Fixture();
        var created = fixture.Create();
        await created.Manager.InitializeAsync();
        await created.Manager.OpenAsync();
        await created.Manager.InstallAsync(fixture.Zip());
        await Until(() => created.Manager.LicenseText.Length > 0);
        var window = new ArtworkWindow { DataContext = created.Manager, Width = width, Height = height };
        fixture.Windows.Add(window);
        window.Show();
        window.SetRenderScaling(scale);
        await Until(() => window.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null));
        Assert.NotNull(window.FindControl<Button>("InstallArtworkButton"));
        Assert.Single(window.FindControl<ListBox>("ArtworkPacks")!.Items);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.Text?.Contains("Example license", StringComparison.Ordinal) == true);
        if (Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal((int)(window.ClientSize.Width * scale), frame.PixelSize.Width);
            Assert.Equal((int)(window.ClientSize.Height * scale), frame.PixelSize.Height);
            var suffix = scale == 1 ? "" : $"-{(int)(scale * 100)}pct";
            frame.Save(Path.Combine(directory, $"artwork-dialog-{width}-{height}{suffix}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    [AvaloniaFact]
    public async Task KeyboardCanApplyRevertRemoveAndCloseArtwork()
    {
        await using var fixture = new Fixture();
        var (editor, session, manager) = fixture.Create();
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip());
        var window = new ArtworkWindow { DataContext = manager };
        fixture.Windows.Add(window);
        window.Show();
        await Until(() => !manager.IsPreviewLoading);
        var before = session.Snapshot;
        Assert.True(window.FindControl<Button>("InstallArtworkButton")!.Focus());
        Press(Key.Tab, PhysicalKey.Tab);
        Assert.Same(window.FindControl<Button>("UseArtworkButton"), window.FocusManager!.GetFocusedElement());
        Press(Key.Space, PhysicalKey.Space);
        await Until(() => editor.Artwork.Icon("slots", "mask") is not null && !manager.IsBusy);
        await Until(() => window.FocusManager.GetFocusedElement() == window.FindControl<Button>("UseArtworkButton"));
        Press(Key.Tab, PhysicalKey.Tab);
        Assert.Same(window.FindControl<Button>("BundledArtworkButton"), window.FocusManager.GetFocusedElement());
        Press(Key.Space, PhysicalKey.Space);
        await Until(() => editor.Artwork.Icon("slots", "mask") is null && !manager.IsBusy);
        await Until(() => window.FocusManager.GetFocusedElement() == window.FindControl<Button>("BundledArtworkButton"));
        Press(Key.Tab, PhysicalKey.Tab);
        Assert.Same(window.FindControl<Button>("RemoveArtworkButton"), window.FocusManager.GetFocusedElement());
        Press(Key.Space, PhysicalKey.Space);
        await Until(() => manager.Rows.IsEmpty && !manager.IsBusy);
        Assert.Null(manager.Problem);
        Assert.Equal(before, session.Snapshot);
        Press(Key.Escape, PhysicalKey.Escape);
        Assert.False(window.IsVisible);

        void Press(Key key, PhysicalKey physical)
        {
            window.KeyPress(key, RawInputModifiers.None, physical, null);
            if (window.IsVisible) { window.KeyRelease(key, RawInputModifiers.None, physical, null); }
        }
    }

    private sealed class Lifetime : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }

    [AvaloniaFact]
    public async Task SharedBitmapHandlesKeepTheirRevisionAliveUntilTheLastViewReleasesIt()
    {
        await using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "image.png");
        File.WriteAllBytes(path, Png);
        var lifetime = new Lifetime();
        using var registration = ArtworkBitmapCache.Register(fixture.Root, lifetime);
        using var first = ArtworkBitmapCache.Acquire(path, 32)!;
        using var second = ArtworkBitmapCache.Acquire(path, 32)!;
        Assert.NotNull(await first.Ready);
        Assert.Same(await first.Ready, await second.Ready);
        registration.Dispose();
        Assert.False(lifetime.Disposed);
        first.Dispose();
        Assert.False(lifetime.Disposed);
        second.Dispose();
        Assert.True(lifetime.Disposed);
    }

    [AvaloniaFact]
    public async Task MissingImagesAreNotCachedForeverAndDetachedViewsReleaseImages()
    {
        await using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "later.png");
        using (var missing = ArtworkBitmapCache.Acquire(path, 32)!) { Assert.Null(await missing.Ready); }
        File.WriteAllBytes(path, Png);
        using var loaded = ArtworkBitmapCache.Acquire(path, 32)!;
        var bitmap = await loaded.Ready;
        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.PixelSize.Width);
        var view = new IconView { Width = 32, Height = 32, Source = path };
        var window = new Window { Content = view };
        fixture.Windows.Add(window);
        window.Show();
        await Until(() => view.GetVisualDescendants().OfType<Image>().Any(image => ReferenceEquals(image.Source, bitmap)));
        window.Close();
        loaded.Dispose();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Image>(), image => image.Source is not null);
    }

    [AvaloniaFact]
    public async Task PartialItemLayerOverridesKeepBundledImagesAndUnrelatedIcons()
    {
        await using var fixture = new Fixture();
        var item = TestPack.Division.Pack.Items.First(item => item.Slot == "mask");
        var backend = new ArtworkPackInstaller(fixture.Store, new ArtworkPngDecoder());
        var zip = fixture.Zip(new { itemArt = new Dictionary<string, object> { [item.ID] = new { icon = "mask" } } });
        var installed = await backend.InstallAsync(zip, TestPack.Division.Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(installed.Succeeded);
        var presentation = new PackPresentation
        {
            ArtworkAssets = [new() { ID = "image", Path = "assets/bundled.png" }, new() { ID = "logo", Path = "assets/logo.png" }],
            ItemArt = ImmutableDictionary<string, ItemArtwork>.Empty.Add(item.ID, new("image", "logo")),
            Icons = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("talents", ImmutableDictionary<string, string>.Empty.Add("talent", "logo"))
        };
        var bundled = new PackArtwork(presentation, fixture.Root);
        var combined = new PackArtwork(bundled, installed.Installed!);
        Assert.Equal(bundled.Item(item.ID, "mask").Image, combined.Item(item.ID, "mask").Image);
        Assert.Equal(Path.Combine(installed.Installed!.Directory, "assets/mask.png"), combined.Item(item.ID, "mask").Icon);
        Assert.Equal(bundled.Icon("talents", "talent"), combined.Icon("talents", "talent"));
    }

    [AvaloniaFact]
    public async Task MainViewLoadsSavedArtworkWithoutChangingTheFreshBuild()
    {
        await using var fixture = new Fixture();
        var backend = new ArtworkPackInstaller(fixture.Store, new ArtworkPngDecoder());
        var installed = await backend.InstallAsync(fixture.Zip(), TestPack.Division.Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(installed.Succeeded);
        await backend.SelectAsync(TestPack.Division.Pack, new(installed.Installed!.Manifest.Id, installed.Installed.ContentDigest), 0, TestContext.Current.CancellationToken);
        using var model = new MainViewModel();
        await model.LoadAsync(Path.Combine(AppContext.BaseDirectory, "packs/division-2"), new FileBuildLibrary(Path.Combine(fixture.Root, "library")),
            AvaloniaDispatcher.Instance, artworkDirectory: fixture.Store);
        Assert.Null(model.Error);
        Assert.NotNull(model.Editor!.Artwork.Icon("slots", "mask"));
        Assert.Equal(0, model.Editor.Session.Snapshot.Revision);
        Assert.Contains("Orange slots", model.Editor.ArtworkManager!.Current, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task InvalidArtworkStartupConfigurationDoesNotDisableTheEditor()
    {
        await using var fixture = new Fixture();
        using var model = new MainViewModel();
        await model.LoadAsync(Path.Combine(AppContext.BaseDirectory, "packs/division-2"), new FileBuildLibrary(Path.Combine(fixture.Root, "library")),
            AvaloniaDispatcher.Instance, artworkDirectory: "");
        Assert.Null(model.Error);
        Assert.NotNull(model.Editor);
        Assert.Null(model.Editor.ArtworkManager);
        Assert.Contains("Artwork settings are unavailable", model.Editor.Message!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task LocalImageTamperingFallsBackAndExactRepairRestoresThePreference()
    {
        await using var fixture = new Fixture();
        var created = fixture.Create(monitor: true);
        await created.Manager.InitializeAsync();
        await created.Manager.OpenAsync();
        await created.Manager.InstallAsync(fixture.Zip());
        await created.Manager.UseCommand.ExecuteAsync(null);
        var path = created.Editor.Artwork.Icon("slots", "mask")!;
        var preferences = File.ReadAllBytes(Path.Combine(fixture.Store, "artwork-settings.json"));
        File.AppendAllText(path, "bad");
        await Until(() => created.Editor.Artwork.Icon("slots", "mask") is null && created.Manager.SelectionProblem is not null);
        File.WriteAllBytes(path, Png);
        await Until(() => created.Editor.Artwork.Icon("slots", "mask") is not null && created.Manager.SelectionProblem is null);
        Assert.Equal(preferences, File.ReadAllBytes(Path.Combine(fixture.Store, "artwork-settings.json")));
        Assert.Equal(0, created.Session.Snapshot.Revision);
    }

    [AvaloniaFact]
    public async Task AnotherWindowsCatalogObservesAnInactiveRevisionRemoval()
    {
        await using var fixture = new Fixture();
        var first = fixture.Create(monitor: true);
        var second = fixture.Create(monitor: true);
        await first.Manager.InitializeAsync();
        await second.Manager.InitializeAsync();
        await first.Manager.OpenAsync();
        await first.Manager.InstallAsync(fixture.Zip());
        await first.Manager.InstallAsync(fixture.Zip(version: "2"));
        await second.Manager.OpenAsync();
        second.Manager.Selected = second.Manager.Rows.Single(row => row.Entry.Pack!.Manifest.Version == "2");
        var secondDirectory = second.Manager.Selected.Entry.Pack!.Directory;
        await Until(() => second.Manager.Previews.Any(preview => preview.Path.StartsWith(secondDirectory, StringComparison.OrdinalIgnoreCase)));
        first.Manager.Selected = first.Manager.Rows.Single(row => row.Entry.Pack!.Manifest.Version == "1");
        await first.Manager.RemoveCommand.ExecuteAsync(null);
        Assert.Null(first.Manager.Problem);
        await Until(() => second.Manager.Rows.Length == 1);
        Assert.Equal("2", Assert.Single(second.Manager.Rows).Entry.Pack!.Manifest.Version);
    }

    [AvaloniaFact]
    public async Task ArtworkSwitchDoesNotInvalidateACompletedSimulation()
    {
        await using var fixture = new Fixture();
        var (editor, session, manager) = fixture.Create();
        Assert.True(editor.Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr")));
        Assert.True(editor.Apply(new ContextOperation("weapon_slot", Text: "primary_weapon")));
        Assert.True(editor.Apply(new ContextOperation("enemy_target", Value: 1)));
        var simulation = editor.Simulate;
        simulation.FightLength = "0.25";
        simulation.Fights = "2";
        simulation.RunCommand.Execute(null);
        await simulation.Running;
        await Until(() => !simulation.IsRunning);
        var result = simulation.Result;
        Assert.NotNull(result);
        var revision = session.Snapshot.Revision;
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip());
        await manager.UseCommand.ExecuteAsync(null);
        Assert.Same(result, simulation.Result);
        await manager.BundledCommand.ExecuteAsync(null);
        Assert.Same(result, simulation.Result);
        Assert.Equal(revision, session.Snapshot.Revision);
    }

    [AvaloniaFact]
    public async Task EscapeClosesTheDialogAndReopeningRestoresItsPreview()
    {
        await using var fixture = new Fixture();
        var (_, _, manager) = fixture.Create();
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip());
        await Until(() => manager.Previews.Length == 1);
        var window = new ArtworkWindow { DataContext = manager };
        fixture.Windows.Add(window);
        window.Show();
        window.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        Assert.Empty(manager.Previews);
        var reopened = new ArtworkWindow { DataContext = manager };
        fixture.Windows.Add(reopened);
        reopened.Show();
        await manager.OpenAsync();
        await Until(() => manager.Previews.Length == 1 && !manager.IsPreviewLoading);
    }

    [AvaloniaFact]
    public async Task HugeInvalidManifestNamesDoNotBecomeUnboundedUiMessages()
    {
        await using var fixture = new Fixture();
        var (_, _, manager) = fixture.Create();
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip(new Dictionary<string, object> { [new string('x', 20_000)] = true }));
        Assert.NotNull(manager.Problem);
        Assert.True(manager.Problem.Length <= 1001);
        Assert.True(manager.Details.Length < 5000);
        Assert.Empty(manager.Rows);
    }
    [AvaloniaFact]
    public async Task AnArtworkPacksNodeAndSpecializationArtReachesTheTreeCards()
    {
        await using var fixture = new Fixture();
        var (editor, _, _) = fixture.Create();
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "gunner");
        var tree = editor.Specialization.Tree;
        TreeCardViewModel Card(string name) => tree.Cards.Single(card => card.Name == name);
        Assert.Null(Card("Incessant").Icon);
        Assert.Null(tree.HubArt);

        // A perk's art can be bound to any of its tiers; the specialization's icon goes to the hub.
        var backend = new ArtworkPackInstaller(fixture.Store, new ArtworkPngDecoder());
        var zip = fixture.Zip(new { icons = new { nodes = new Dictionary<string, string> { ["gunner_incessant_tier_2"] = "mask" }, specializations = new { gunner = "mask" } } });
        var installed = await backend.InstallAsync(zip, TestPack.Division.Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(installed.Succeeded);
        var revision = editor.Session.Snapshot.Revision;
        editor.SetArtwork(new PackArtwork(PackArtwork.None, installed.Installed!));
        var art = Path.Combine(installed.Installed!.Directory, "assets/mask.png");
        Assert.Equal(art, Card("Incessant").Icon);
        Assert.Null(Card("Coupler").Icon);
        Assert.Equal(art, tree.HubArt);
        Assert.Equal(revision, editor.Session.Snapshot.Revision);

        // Another specialization and back: the cards are new, and take the art as they're laid out.
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "technician");
        Assert.Null(tree.HubArt);
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "gunner");
        Assert.Equal(art, Card("Incessant").Icon);

        var window = new SpecializationTreeWindow { DataContext = tree };
        fixture.Windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var views = window.GetVisualDescendants().OfType<IconView>().Where(view => view.Name == "TreeCardArt").ToList();
        Assert.Equal(tree.Cards.Length, views.Count);
        Assert.Single(views, view => view.Source == art);
        Assert.Equal(art, window.GetVisualDescendants().OfType<IconView>().Single(view => view.Name == "TreeHubArt").Source);

        // Back to no art: the app's own symbols.
        editor.SetArtwork(PackArtwork.None);
        Assert.All(tree.Cards, card => Assert.Null(card.Icon));
        Assert.Null(tree.HubArt);
    }
}
