using CharacterPlanner.Application.Assumptions;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Infrastructure.Artwork;
using CharacterPlanner.Infrastructure.Sessions;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The window's content: loading the pack, then the editor, or why the pack couldn't be loaded. The pack is
/// loaded and checked (artwork included) off the UI thread, so the window appears at once.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private string title = "Character Planner";
    private string? error;
    private EditorViewModel? editor;
    private bool disposed;
    private SessionPipeHost? sessionHost;

    public string Status { get; private set => SetProperty(ref field, value); } = "Loading the Division 2 pack…";

    /// <summary>The window's title: the pack's name once it has loaded, as "The Division 2 — Character Planner".</summary>
    public string Title { get => title; private set => SetProperty(ref title, value); }

    /// <summary>Why the app can't edit builds, or null.</summary>
    public string? Error { get => error; private set => SetProperty(ref error, value); }

    public EditorViewModel? Editor { get => editor; private set => SetProperty(ref editor, value); }

    // An unexpected failure reported while the pack was still loading, for the editor to show.
    private string? pendingReport;

    /// <param name="loaded">Called with the pack once it has loaded, before the editor shows; the app applies its theme.</param>
    /// <param name="assumptionSets">The saved assumption sets (F45); without a store the Assumptions tab has no set bar.</param>
    /// <param name="goLibrary">The one-time import of the Go app's library, run before the library is listed; none in tests that don't need it.</param>
    /// <param name="sessionRegistry">Where to register the window for live CLI control (ADR 0007); null leaves it unregistered.</param>
    public async Task LoadAsync(string packDirectory, IBuildLibrary library, IUiDispatcher ui, Action<GamePack>? loaded = null, IAssumptionSetStore? assumptionSets = null,
        GoLibraryImport? goLibrary = null, string? sessionRegistry = null, string? artworkDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(ui);
        try
        {
            var read = await PackFileLoader.LoadAsync(packDirectory);
            if (disposed) { return; }
            if (read.Validation.Compiled is not { } compiled)
            {
                var problems = read.Validation.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToList();
                Error = problems.Count == 0
                    ? $"The Division 2 pack at {read.Directory} couldn't be loaded."
                    : $"The Division 2 pack at {read.Directory} has {problems.Count} problem{(problems.Count == 1 ? "" : "s")}, the first: {problems[0].Message}";
                Status = "The pack couldn't be loaded.";
                return;
            }
            loaded?.Invoke(compiled.Pack);
            Title = $"{compiled.Pack.Name} — Character Planner";
            // Share codes in either format, Go-era ones included, for party members and Import (ADR 0002, ADR 0003).
            var order = compiled.Pack.LegacyShareOrder;
            ImportedBuild ReadShareCode(string code)
            {
                var import = ShareCodeReader.Read(code, order);
                return new(import.Build, import.DroppedFields);
            }
            var session = new PlannerSession(compiled, PlannerState.Of(BuildSnapshot.Create(compiled.Pack)), new OperationApplier(compiled, code => ReadShareCode(code).Build));
            Editor = new EditorViewModel(session, library, ui, new PackArtwork(compiled.Pack.Presentation, read.ArtworkWithheld ? null : read.Directory), assumptionSets) { ReadShareCode = ReadShareCode };
            if (disposed) { Editor.Dispose(); return; }
            Status = $"{compiled.Pack.Name} {compiled.Pack.Version}";
            if (sessionRegistry is not null)
            {
                // The window works without live control; a failure to host only says so.
                try
                {
                    var host = await SessionPipeHost.StartAsync(session, read.Directory, read.ManifestSha256, sessionRegistry);
                    if (disposed) { await host.DisposeAsync(); return; }
                    sessionHost = host;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Status += " · live session control is unavailable: " + exception.Message; }
            }
            if (goLibrary is not null)
            {
                try
                {
                    if (await goLibrary.RunAsync(library, compiled.Pack.ID) is { } imported) { Status += " · " + imported.Summary; }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Editor.Message = $"The Go app's library couldn't be imported: {exception.Message}"; }
            }
            await Editor.RefreshLibraryCommand.ExecuteAsync(null);
            await Editor.AssumptionsTab.Sets.RefreshAsync();
            if (disposed) { return; }
            string? artworkProblem = null;
            if (artworkDirectory is not null)
            {
                try
                {
                    var artwork = new ArtworkPackInstaller(artworkDirectory, new ArtworkPngDecoder());
                    var manager = new ArtworkManagerViewModel(Editor, Editor.Artwork, artwork, artwork, ui, artworkDirectory);
                    Editor.ArtworkManager = manager;
                    if (disposed) { manager.Dispose(); return; }
                    await manager.InitializeAsync();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { Editor.ArtworkManager?.Dispose(); Editor.ArtworkManager = null; artworkProblem = "Artwork settings are unavailable: " + exception.Message; }
                if (disposed) { Editor.ArtworkManager?.Dispose(); return; }
            }
            if (artworkProblem is not null) { Editor.Message = Editor.Message is null ? artworkProblem : Editor.Message + "\n" + artworkProblem; }
            // A failure reported while loading follows the startup messages, so the loading page's hiding doesn't lose it.
            if (pendingReport is { } report)
            {
                Editor.Message = Editor.Message is null ? report : Editor.Message + "\n" + report;
                pendingReport = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"The Division 2 pack couldn't be read from {packDirectory}: {exception.Message}";
            Status = "The pack couldn't be loaded.";
        }
#pragma warning disable CA1031 // Startup runs unobserved; any failure must reach the window rather than leave it loading forever.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Error = $"The planner couldn't start: {exception.Message}";
            Status = "The pack couldn't be loaded.";
        }
    }

    /// <summary>
    /// Shows an unexpected failure (F94): as the editor's message once it's open. Before, on the loading page, and the editor
    /// takes it over when it opens, so it isn't lost when that page hides.
    /// </summary>
    public void ReportUnexpected(string message)
    {
        if (Editor is { } open) { open.Message = message; return; }
        pendingReport = message;
        Status = message;
    }

    /// <summary>Disposes the editor, including one a load still in progress creates afterwards.</summary>
    public void Dispose()
    {
        disposed = true;
        editor?.Dispose();
        // Disposing removes the registration at once, so the CLI stops listing the window; stopping the pipe finishes in
        // the background (bounded, never throwing) rather than holding up the closing UI thread.
        _ = sessionHost?.DisposeAsync().AsTask();
    }
}
