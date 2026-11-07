using System.Collections.Immutable;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.Views;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record ArtworkPackRow(ArtworkCatalogEntry Entry)
{
    public string Name => Entry.Pack?.Manifest.Name ?? Entry.Reference.Id;
    public string Detail => Entry.Pack is { } pack ? $"{pack.Manifest.Version} · {pack.Manifest.Author}" : "Unavailable — see details";
    public bool Usable => Entry.Usable;
    public string Coverage
    {
        get
        {
            if (Entry.Pack is not { } pack) { return ""; }
            var images = pack.Manifest.Assets.Length;
            var bindings = pack.ApplicableBindings.Icons.Values.Sum(map => map.Count) + pack.ApplicableBindings.ItemArt.Count + pack.ApplicableBindings.SpecializationImages.Count;
            return $"{images} {(images == 1 ? "image" : "images")} · {bindings} {(bindings == 1 ? "binding" : "bindings")}";
        }
    }
}

public sealed record ArtworkPreview(string Path, string Name);

/// <summary>UI-thread appearance workflow. Backend work runs off-thread; generations reject stale callbacks and leases protect displayed images.</summary>
public sealed class ArtworkManagerViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly EditorViewModel editor;
    private readonly PackArtwork bundled;
    private readonly IArtworkPackInstaller installer;
    private readonly IArtworkPackStore store;
    private readonly IUiDispatcher ui;
    private readonly string? directory;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private TaskCompletionSource? operationFinished;
    private CancellationTokenSource? activeRefresh;
    private CancellationTokenSource? previewRefresh;
    private ArtworkStoreMonitor? monitor;
    private IDisposable? activeOwner;
    private IDisposable? previewOwner;
    private readonly Dictionary<ArtworkPackReference, List<Task>> retirements = [];
    private readonly HashSet<Task> activeRuns = [];
    private readonly HashSet<Task> backgroundRuns = [];
    private ArtworkRevisionState? previewState;
    private ArtworkPackReference? desired;
    private ArtworkPackReference? displayed;
    private ArtworkPackReference? previewReference;
    private long activeGeneration;
    private long catalogGeneration;
    private long previewGeneration;
    private long noticeGeneration;
    private long selectionRevision;
    private bool disposed;
    private bool dialogOpen;
    private bool monitorPending;
    private ArtworkPackRow? selected;
    private ArtworkCredit? selectedCredit;

    public ArtworkManagerViewModel(EditorViewModel editor, PackArtwork bundled, IArtworkPackInstaller installer, IArtworkPackStore store, IUiDispatcher ui, string? directory = null)
    {
        (this.editor, this.bundled, this.installer, this.store, this.ui, this.directory) = (editor, bundled, installer, store, ui, directory);
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(RefreshDialogAsync), () => !IsBusy);
        UseCommand = new AsyncRelayCommand(() => RunAsync(UseAsync), () => !IsBusy && Selected?.Usable == true);
        BundledCommand = new AsyncRelayCommand(() => RunAsync(BundledAsync), () => !IsBusy);
        RemoveCommand = new AsyncRelayCommand(() => RunAsync(RemoveAsync), () => !IsBusy && Selected is not null);
        RecoverCommand = new AsyncRelayCommand(() => RunAsync(RecoverAsync), () => !IsBusy && NeedsRecovery);
        CancelCommand = new RelayCommand(() => operation?.Cancel(), () => IsBusy);
    }

    public ImmutableArray<ArtworkPackRow> Rows { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasNoPacks)); } } } = [];
    public bool HasNoPacks => Rows.IsEmpty && !IsBusy;
    public ImmutableArray<ArtworkPreview> Previews { get; private set => SetProperty(ref field, value); } = [];
    public ImmutableArray<ArtworkCredit> Credits { get; private set => SetProperty(ref field, value); } = [];
    public string LicenseText { get; private set => SetProperty(ref field, value); } = "";
    public string Current { get; private set => SetProperty(ref field, value); } = "Using bundled artwork";
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public string? Problem { get; private set => SetProperty(ref field, value); }
    public string? SelectionProblem { get; private set => SetProperty(ref field, value); }
    public string Details { get; private set => SetProperty(ref field, value); } = "";
    public bool IsBusy { get; private set { if (SetProperty(ref field, value)) { CommandsChanged(); OnPropertyChanged(nameof(HasNoPacks)); } } }
    public bool IsPreviewLoading { get; private set => SetProperty(ref field, value); }
    public bool NeedsRecovery { get; private set { if (SetProperty(ref field, value)) { RecoverCommand?.NotifyCanExecuteChanged(); } } }
    public string RemoveLabel => Selected is { } row && row.Entry.Reference == desired ? "Use bundled and remove" : "Remove";
    public ArtworkPackRow? Selected
    {
        get => selected;
        set { if (value is not null) { if (value != selected) { Problem = null; Details = ""; } SelectRow(value); } }
    }
    public ArtworkCredit? SelectedCredit
    {
        get => selectedCredit;
        set { if (value is not null && SetProperty(ref selectedCredit, value)) { _ = ReadNoticeAsync(value); } }
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand UseCommand { get; }
    public IAsyncRelayCommand BundledCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand RecoverCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        if (directory is not null)
        {
            try { monitor = new(directory, () => ui.Post(() => { if (!disposed) { _ = RefreshFromMonitorAsync(); } })); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Problem = "Artwork changes may need Refresh: " + exception.Message; }
        }
        await RefreshActiveAsync();
    }

    public async Task OpenAsync()
    {
        if (disposed) { return; }
        dialogOpen = true;
        try
        {
            if (IsBusy)
            {
                Cancel(operation);
                await (operationFinished?.Task ?? Task.CompletedTask).WaitAsync(lifetime.Token);
            }
            if (!disposed && dialogOpen) { await RunAsync(RefreshDialogAsync); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void FilePickerFailed(string message) => Problem = "Artwork ZIP could not be opened: " + message;
    public void CloseDialog() { dialogOpen = false; operation?.Cancel(); ClearPreview(); }
    public Task InstallAsync(string path) => RunAsync(async token =>
    {
        var progress = new Progress<ArtworkInstallProgress>(value => { if (!disposed) { Status = $"Checking artwork: {value.CompletedFiles} / {value.TotalFiles} files"; } });
        var result = await Task.Run(() => installer.InstallAsync(path, editor.Pack, progress, token), token);
        Report(result.Diagnostics);
        if (!result.Succeeded) { return; }
        Status = $"Installed {result.Installed!.Manifest.Name}. Choose Use this pack to apply it.";
        await RefreshCatalogAsync(token, new(result.Installed.Manifest.Id, result.Installed.ContentDigest));
    });

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (disposed || IsBusy) { return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationFinished = finished;
        IsBusy = true;
        Problem = null;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!disposed) { Status = "Artwork operation canceled"; } }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { if (!disposed) { Problem = "Artwork operation failed: " + exception.Message; } }
        finally
        {
            operation = null;
            if (!disposed)
            {
                IsBusy = false;
                if (monitorPending) { monitorPending = false; _ = RefreshFromMonitorAsync(); }
            }
            finished.TrySetResult();
        }
    }

    private async Task RefreshDialogAsync(CancellationToken token) { await RefreshActiveAsync(); await RefreshCatalogAsync(token); }
    private async Task RefreshFromMonitorAsync()
    {
        if (IsBusy) { monitorPending = true; return; }
        await RefreshActiveAsync();
        if (IsBusy) { monitorPending = true; return; }
        if (dialogOpen && !disposed) { await RefreshCatalogAsync(lifetime.Token, background: true); }
    }

    public async Task RefreshActiveAsync()
    {
        Cancel(activeRefresh);
        var cancellation = operation is { } foreground
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, foreground.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeRefresh = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeRuns.Add(finished.Task);
        var generation = ++activeGeneration;
        try
        {
            var state = await Task.Run(() => store.LoadSelectionAsync(editor.Pack, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            await PublishAsync(() =>
            {
                if (disposed || generation != activeGeneration) { state.Dispose(); return; }
                selectionRevision = state.Revision;
                desired = state.Selected;
                NeedsRecovery = state.Diagnostics.Any(value => value.Code == "artwork-settings-invalid") || state.Revision == long.MaxValue;
                var reference = state.Active is { } active ? new ArtworkPackReference(active.Pack.Manifest.Id, active.Pack.ContentDigest) : null;
                Current = state.Active is { } shown ? $"Using {shown.Pack.Manifest.Name} · {shown.Pack.Manifest.Version}" : "Using bundled artwork";
                Report(state.Diagnostics, selection: true);
                if (reference == displayed) { state.Dispose(); }
                else
                {
                    IDisposable next = state.Active is { } lease ? ArtworkBitmapCache.Register(lease.Pack.Directory, state) : state;
                    var previous = activeOwner;
                    var previousReference = displayed;
                    activeOwner = next;
                    displayed = reference;
                    editor.SetArtwork(state.Active is { } selectedPack ? new PackArtwork(bundled, selectedPack.Pack) : bundled);
                    Retire(previous, previousReference);
                }
                OnPropertyChanged(nameof(RemoveLabel));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            await PublishAsync(() =>
            {
                if (ReferenceEquals(activeRefresh, cancellation)) { activeRefresh = null; }
                cancellation.Dispose();
                activeRuns.Remove(finished.Task);
                finished.TrySetResult();
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshCatalogAsync(CancellationToken token, ArtworkPackReference? prefer = null, bool background = false)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backgroundRuns.Add(finished.Task);
        var generation = ++catalogGeneration;
        try
        {
            var catalog = await Task.Run(() => store.ListAsync(editor.Pack, token), token);
            if (disposed || generation != catalogGeneration || !dialogOpen || background && IsBusy) { return; }
            var keep = prefer ?? Selected?.Entry.Reference;
            Rows = [.. catalog.Entries.Select(entry => new ArtworkPackRow(entry))];
            SelectRow(Rows.FirstOrDefault(row => row.Entry.Reference == keep) ?? Rows.FirstOrDefault());
            if (previewState is null && previewRefresh is null && Selected?.Usable == true) { _ = PreviewAsync(Selected); }
            if (!catalog.Diagnostics.IsEmpty) { Report(catalog.Diagnostics); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { backgroundRuns.Remove(finished.Task); finished.TrySetResult(); }
    }

    private void SelectRow(ArtworkPackRow? row)
    {
        if (!SetProperty(ref selected, row, nameof(Selected))) { return; }
        OnPropertyChanged(nameof(RemoveLabel));
        CommandsChanged();
        _ = PreviewAsync(row);
    }

    private async Task PreviewAsync(ArtworkPackRow? row)
    {
        Cancel(previewRefresh);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewRefresh = cancellation;
        var generation = ++previewGeneration;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backgroundRuns.Add(finished.Task);
        if (row is not null) { Track(row.Entry.Reference, finished.Task); }
        try
        {
            if (row?.Usable != true) { ClearPreview(); if (row is not null) { Report(row.Entry.Diagnostics); } return; }
            IsPreviewLoading = true;
            var state = await Task.Run(() => store.OpenRevisionAsync(editor.Pack, row.Entry.Reference, cancellation.Token), cancellation.Token);
            if (disposed || !dialogOpen || generation != previewGeneration) { state.Dispose(); return; }
            if (!state.Succeeded) { ClearPreview(); Report(state.Diagnostics); return; }
            if (previewReference == row.Entry.Reference && previewState is not null) { state.Dispose(); return; }
            var previous = previewOwner;
            var previousReference = previewReference;
            previewState = state;
            previewReference = row.Entry.Reference;
            previewOwner = ArtworkBitmapCache.Register(state.Active!.Pack.Directory, state);
            Previews = [.. state.Active.Pack.Manifest.Assets.Take(6).Select(asset => new ArtworkPreview(Path.Combine(state.Active.Pack.Directory, asset.Path), asset.Id))];
            Credits = state.Active.Pack.Manifest.Credits;
            selectedCredit = null;
            OnPropertyChanged(nameof(SelectedCredit));
            SelectedCredit = Credits.FirstOrDefault();
            Retire(previous, previousReference);
            Report(state.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(previewRefresh, cancellation)) { previewRefresh = null; }
            if (generation == previewGeneration && !disposed) { IsPreviewLoading = false; }
            backgroundRuns.Remove(finished.Task);
            finished.TrySetResult();
        }
    }

    private async Task ReadNoticeAsync(ArtworkCredit credit)
    {
        var generation = ++noticeGeneration;
        var preview = previewState;
        LicenseText = "";
        if (preview?.Active is null) { return; }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backgroundRuns.Add(finished.Task);
        if (previewReference is { } reference) { Track(reference, finished.Task); }
        try
        {
            var text = await Task.Run(() => preview.Active.ReadNoticeAsync(credit.LicenseFile, lifetime.Token), lifetime.Token);
            if (!disposed && generation == noticeGeneration && ReferenceEquals(previewState, preview)) { LicenseText = text; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        { if (!disposed && generation == noticeGeneration) { Problem = "License notice could not be read: " + exception.Message; } }
        finally { backgroundRuns.Remove(finished.Task); finished.TrySetResult(); }
    }

    private void ClearPreview()
    {
        ++previewGeneration;
        ++noticeGeneration;
        Cancel(previewRefresh);
        var previousReference = previewReference;
        previewState = null;
        previewReference = null;
        Previews = [];
        Credits = [];
        selectedCredit = null;
        OnPropertyChanged(nameof(SelectedCredit));
        LicenseText = "";
        IsPreviewLoading = false;
        var previous = previewOwner;
        previewOwner = null;
        Retire(previous, previousReference);
    }

    private async Task UseAsync(CancellationToken token)
    {
        if (Selected?.Entry.Reference is not { } reference) { return; }
        var expected = selectionRevision;
        var result = await Task.Run(() => store.SelectAsync(editor.Pack, reference, expected, token), token);
        Report(result.Diagnostics);
        await RefreshActiveAsync();
        if (result.Succeeded) { Status = Current; }
    }
    private async Task BundledAsync(CancellationToken token)
    {
        var expected = selectionRevision;
        var result = await Task.Run(() => store.SelectAsync(editor.Pack, null, expected, token), token);
        Report(result.Diagnostics);
        await RefreshActiveAsync();
        if (result.Succeeded) { Status = Current; }
    }
    private async Task RemoveAsync(CancellationToken token)
    {
        if (Selected?.Entry.Reference is not { } reference) { return; }
        var revisionDirectory = Selected.Entry.Pack?.Directory;
        if (reference == desired)
        {
            var beforeBundled = selectionRevision;
            var bundledResult = await Task.Run(() => store.SelectAsync(editor.Pack, null, beforeBundled, token), token);
            Report(bundledResult.Diagnostics);
            if (!bundledResult.Succeeded) { await RefreshActiveAsync(); return; }
            await RefreshActiveAsync();
        }
        ClearPreview();
        Status = "Waiting for displayed artwork and pending image reads to close; Cancel operation stops waiting";
        await Task.WhenAll(activeRuns.ToArray()).WaitAsync(token);
        if (retirements.TryGetValue(reference, out var pending))
        {
            await Task.WhenAll(pending).WaitAsync(token);
            retirements.Remove(reference);
        }
        if (revisionDirectory is not null) { await ArtworkBitmapCache.WaitForReleaseAsync(revisionDirectory, token); }
        var expected = selectionRevision;
        var result = await Task.Run(() => store.RemoveAsync(reference, expected, token), token);
        Report(result.Diagnostics);
        if (result.Succeeded) { Status = "Artwork revision removed"; }
        await RefreshCatalogAsync(token);
    }
    private async Task RecoverAsync(CancellationToken token)
    {
        var result = await Task.Run(() => store.ResetInvalidSettingsAsync(token), token);
        Report(result.Diagnostics);
        await RefreshActiveAsync();
        await RefreshCatalogAsync(token);
    }

    private void Report(ImmutableArray<ArtworkDiagnostic> diagnostics, bool selection = false)
    {
        var message = diagnostics.FirstOrDefault(value => value.Severity == "error")?.Message;
        var error = message is null ? null : Short(message, 1000);
        if (selection) { SelectionProblem = error; }
        else if (error is not null) { Problem = error; }
        if (diagnostics.IsEmpty) { return; }
        Details = string.Join("\n", diagnostics.Take(100).Select(value => $"{value.Code} ({Short(value.Path, 256)}): {Short(value.Message, 1024)}"));
        if (diagnostics.Length > 100) { Details += $"\nShowing the first 100 of {diagnostics.Length} diagnostics."; }
    }
    private static string Short(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
    private void Retire(IDisposable? owner, ArtworkPackReference? reference)
    {
        owner?.Dispose();
        foreach (var key in retirements.Keys.ToArray())
        {
            retirements[key].RemoveAll(task => task.IsCompleted);
            if (retirements[key].Count == 0) { retirements.Remove(key); }
        }
        if (reference is not null && owner is ArtworkBitmapCache.Registration registration && !registration.Released.IsCompleted)
        {
            Track(reference, registration.Released);
        }
    }
    private void Track(ArtworkPackReference reference, Task task)
    {
        if (!retirements.TryGetValue(reference, out var pending)) { pending = []; retirements.Add(reference, pending); }
        pending.RemoveAll(value => value.IsCompleted);
        pending.Add(task);
    }
    private static void Cancel(CancellationTokenSource? cancellation)
    { try { cancellation?.Cancel(); } catch (ObjectDisposedException) { /* A completed background refresh already retired its token. */ } }
    private void CommandsChanged()
    {
        foreach (var command in new[] { RefreshCommand, UseCommand, BundledCommand, RemoveCommand, RecoverCommand }) { command?.NotifyCanExecuteChanged(); }
        CancelCommand?.NotifyCanExecuteChanged();
    }
    private Task PublishAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() => { try { action(); completion.TrySetResult(); } catch (Exception exception) { completion.TrySetException(exception); } });
        return completion.Task;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        lifetime.Cancel();
        monitor?.Dispose();
        ClearPreview();
        activeOwner?.Dispose();
        activeOwner = null;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await (operationFinished?.Task ?? Task.CompletedTask);
        await Task.WhenAll(activeRuns.Concat(backgroundRuns).ToArray());
    }
}
