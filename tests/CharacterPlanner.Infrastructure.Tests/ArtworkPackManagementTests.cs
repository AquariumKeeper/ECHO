using System.Text;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.Artwork;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed partial class ArtworkPackInstallerTests
{
    private string SettingsFile => Path.Combine(Store, "artwork-settings.json");
    private ArtworkPackInstaller Manager => new(Store, new Decoder());

    [Fact]
    public async Task VerifiedImageDigestsAvoidRepeatedDecodesButStillDetectSameLengthTampering()
    {
        var decoder = new Decoder();
        var store = new ArtworkPackInstaller(Store, decoder);
        var installed = await store.InstallAsync(Zip(Files()), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(installed.Succeeded);
        var reference = new ArtworkPackReference(installed.Installed!.Manifest.Id, installed.Installed.ContentDigest);
        Assert.Equal(1, decoder.Calls);
        Assert.True((await store.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken)).Succeeded);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            Assert.Single((await store.ListAsync(Pack, TestContext.Current.CancellationToken)).Entries);
            using var state = await store.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
            Assert.False(state.UsesBundled);
        }));
        Assert.Equal(1, decoder.Calls);
        var path = Path.Combine(installed.Installed.Directory, "assets/placeholder.png");
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        using var damaged = await store.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(damaged.UsesBundled);
        Assert.Contains(damaged.Diagnostics, value => value.Code == "artwork-hash-mismatch");
        Assert.Equal(1, decoder.Calls);
    }

    [Fact]
    public async Task ResetQuarantinesALinkWithoutReadingOrChangingItsTarget()
    {
        Directory.CreateDirectory(Store);
        var target = Path.Combine(root, "settings-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        if (OperatingSystem.IsWindows())
        {
            // Junctions exercise directory links without requiring Windows symlink privileges.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{SettingsFile}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        else { Directory.CreateSymbolicLink(SettingsFile, target); }
        var manager = Manager;
        using (var state = await manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken))
        { Assert.Contains(state.Diagnostics, value => value.Code == "artwork-settings-invalid"); }
        var reset = await manager.ResetInvalidSettingsAsync(TestContext.Current.CancellationToken);
        Assert.True(reset.Succeeded);
        var backup = Assert.Single(reset.Diagnostics).Path;
        Assert.True((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        using var recovered = await manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(recovered.UsesBundled);
        Assert.Empty(recovered.Diagnostics);
        Directory.Delete(backup);
    }

    private async Task<ArtworkPackReference> InstallManagedAsync(string version = "1", string? id = null, GamePack? game = null, Action<JsonObject>? edit = null)
    {
        var files = Files();
        Manifest(files, manifest =>
        {
            manifest["version"] = version;
            if (id is not null) { manifest["id"] = id; }
            if (game is not null) { manifest["gamePackId"] = game.ID; }
            edit?.Invoke(manifest);
        });
        var result = await Manager.InstallAsync(Zip(files), game ?? Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(value => value.Message)));
        return new(result.Installed!.Manifest.Id, result.Installed.ContentDigest);
    }

    private static void ChangeError(ArtworkStoreChange result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public async Task EmptyCatalogAndSelectionDoNotCreateUserData()
    {
        var catalog = await Manager.ListAsync(Pack, TestContext.Current.CancellationToken);
        Assert.Equal(0, catalog.SelectionRevision);
        Assert.Empty(catalog.Entries);
        using var state = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(state.UsesBundled);
        Assert.Null(state.Selected);
        Assert.Equal(0, state.Revision);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task SelectionSurvivesANewStoreInstanceAndBundledModeClearsOnlyThePreference()
    {
        var reference = await InstallManagedAsync();
        Assert.False(File.Exists(SettingsFile));
        var selected = await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        Assert.True(selected.Succeeded);
        Assert.True(selected.Changed);
        Assert.Equal(1, selected.SelectionRevision);
        using (var state = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken))
        {
            Assert.False(state.UsesBundled);
            Assert.Equal(reference, state.Selected);
            Assert.Equal(reference.ContentDigest, state.Active!.Pack.ContentDigest);
        }
        var bundled = await Manager.SelectAsync(Pack, null, 1, TestContext.Current.CancellationToken);
        Assert.True(bundled.Succeeded);
        Assert.Equal(2, bundled.SelectionRevision);
        using var fallback = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(fallback.UsesBundled);
        Assert.Null(fallback.Selected);
        Assert.Single((await Manager.ListAsync(Pack, TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    public async Task NoOpSelectionPreservesTheRevisionAndSettingsBytes()
    {
        var first = await Manager.SelectAsync(Pack, null, 0, TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded);
        Assert.False(first.Changed);
        Assert.False(File.Exists(SettingsFile));
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(SettingsFile);
        var repeated = await Manager.SelectAsync(Pack, reference, 1, TestContext.Current.CancellationToken);
        Assert.True(repeated.Succeeded);
        Assert.False(repeated.Changed);
        Assert.Equal(1, repeated.SelectionRevision);
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public async Task StaleAndConcurrentSelectionsCannotOverwriteAFresherChoice()
    {
        var reference = await InstallManagedAsync();
        var other = await InstallManagedAsync("2");
        var results = await Task.WhenAll(Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken),
            Manager.SelectAsync(Pack, other, 0, TestContext.Current.CancellationToken));
        Assert.Single(results, result => result.Succeeded);
        ChangeError(Assert.Single(results, result => !result.Succeeded), "artwork-selection-conflict");
        var original = File.ReadAllBytes(SettingsFile);
        ChangeError(await Manager.SelectAsync(Pack, null, 0, TestContext.Current.CancellationToken), "artwork-selection-conflict");
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public async Task SelectionRechecksItsRevisionAfterSlowImageValidation()
    {
        var reference = await InstallManagedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ArtworkPackInstaller(Store, new Decoder
        {
            Decode = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(1, 1);
        }
        });
        var pending = slow.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try { Assert.True((await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken)).Succeeded); }
        finally { release.TrySetResult(); }
        ChangeError(await pending, "artwork-selection-conflict");
    }

    [Theory]
    [InlineData("image")]
    [InlineData("license")]
    [InlineData("extra")]
    [InlineData("manifest")]
    [InlineData("missing")]
    public async Task StartupRevalidationFallsBackWithoutLosingTheSavedChoice(string damage)
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(SettingsFile);
        var directory = Path.Combine(Store, reference.Id, reference.ContentDigest);
        if (damage == "image") { File.AppendAllText(Path.Combine(directory, "assets/placeholder.png"), "tampered"); }
        if (damage == "license") { File.AppendAllText(Path.Combine(directory, "licenses/LICENSE.txt"), "tampered"); }
        if (damage == "extra") { File.WriteAllText(Path.Combine(directory, "unexpected.txt"), "extra"); }
        if (damage == "manifest") { File.WriteAllText(Path.Combine(directory, "artwork.json"), "{}"); }
        if (damage == "missing") { Directory.Delete(directory, recursive: true); }
        using var state = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(state.UsesBundled);
        Assert.Equal(reference, state.Selected);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
        Assert.True((await Manager.SelectAsync(Pack, null, 1, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task CatalogKeepsCorruptEntriesVisibleAndIgnoresPrivateStaging()
    {
        var good = await InstallManagedAsync();
        var bad = await InstallManagedAsync("2");
        File.WriteAllText(Path.Combine(Store, bad.Id, bad.ContentDigest, "artwork.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Store, ".staging-unfinished"));
        Directory.CreateDirectory(Path.Combine(Store, ".removed-unfinished"));
        var catalog = await Manager.ListAsync(Pack, TestContext.Current.CancellationToken);
        Assert.Equal(2, catalog.Entries.Length);
        Assert.True(Assert.Single(catalog.Entries, entry => entry.Reference == good).Usable);
        Assert.False(Assert.Single(catalog.Entries, entry => entry.Reference == bad).Usable);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Store, ".leases"), "*.lease", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DataUpdatesReevaluateCoverageInsteadOfPinningTheDataVersion()
    {
        var reference = await InstallManagedAsync(edit: manifest => manifest["bindings"]!["icons"]!["slots"]!["new_slot"] = "placeholder");
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var updated = Pack with { Version = "later", Slots = [.. Pack.Slots, new() { ID = "new_slot", Name = "New" }] };
        using var state = await Manager.LoadSelectionAsync(updated, TestContext.Current.CancellationToken);
        Assert.False(state.UsesBundled);
        Assert.Equal(2, state.Active!.Pack.ApplicableBindings.Icons["slots"].Count);
        using var missing = await Manager.LoadSelectionAsync(Pack with { Slots = [] }, TestContext.Current.CancellationToken);
        Assert.True(missing.UsesBundled);
        Assert.Equal(reference, missing.Selected);
        Assert.Contains(missing.Diagnostics, problem => problem.Code == "artwork-no-coverage");
    }

    [Fact]
    public async Task InvalidNewSelectionLeavesThePreviousPreferenceUnchanged()
    {
        var good = await InstallManagedAsync();
        var bad = await InstallManagedAsync("2");
        await Manager.SelectAsync(Pack, good, 0, TestContext.Current.CancellationToken);
        File.AppendAllText(Path.Combine(Store, bad.Id, bad.ContentDigest, "assets/placeholder.png"), "changed");
        var original = File.ReadAllBytes(SettingsFile);
        ChangeError(await Manager.SelectAsync(Pack, bad, 1, TestContext.Current.CancellationToken), "artwork-hash-mismatch");
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public async Task SelectedAndLeasedRevisionsCannotBeRemoved()
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        ChangeError(await Manager.RemoveAsync(reference, 1, TestContext.Current.CancellationToken), "artwork-selected");
        using var first = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        using var second = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        await Manager.SelectAsync(Pack, null, 1, TestContext.Current.CancellationToken);
        ChangeError(await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken), "artwork-in-use");
        first.Dispose();
        ChangeError(await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken), "artwork-in-use");
        second.Dispose();
        var removed = await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken);
        Assert.True(removed.Succeeded);
        Assert.True(removed.Changed);
        Assert.False(Directory.Exists(Path.Combine(Store, reference.Id, reference.ContentDigest)));
        Assert.Empty((await Manager.ListAsync(Pack, TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    public async Task RemoveIsIdempotentAndRejectsStaleSelections()
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        await Manager.SelectAsync(Pack, null, 1, TestContext.Current.CancellationToken);
        ChangeError(await Manager.RemoveAsync(reference, 1, TestContext.Current.CancellationToken), "artwork-selection-conflict");
        Assert.True((await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken)).Changed);
        var repeated = await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken);
        Assert.True(repeated.Succeeded);
        Assert.False(repeated.Changed);
    }

    [Theory]
    [InlineData("../outside", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("example", "../outside")]
    [InlineData("CON", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task CallerReferencesCannotEscapeTheStore(string id, string digest)
    {
        var reference = new ArtworkPackReference(id, digest);
        ChangeError(await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken), "artwork-reference-invalid");
        ChangeError(await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken), "artwork-reference-invalid");
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"schemaVersion":1,"revision":0,"selections":{},"extra":true}""")]
    [InlineData("""{"schemaVersion":1,"revision":0,"revision":1,"selections":{}}""")]
    [InlineData("""{"schemaVersion":1,"revision":0,"selections":{"division_2":null}}""")]
    [InlineData("""{"schemaVersion":1,"revision":-1,"selections":{}}""")]
    public async Task InvalidSettingsFallBackAndArePreservedUntilExplicitRecovery(string json)
    {
        await InstallManagedAsync();
        File.WriteAllText(SettingsFile, json);
        using var state = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(state.UsesBundled);
        Assert.Equal(-1, state.Revision);
        Assert.Contains(state.Diagnostics, problem => problem.Code == "artwork-settings-invalid");
        ChangeError(await Manager.SelectAsync(Pack, null, -1, TestContext.Current.CancellationToken), "artwork-settings-invalid");
        Assert.Equal(json, File.ReadAllText(SettingsFile));
        var reset = await Manager.ResetInvalidSettingsAsync(TestContext.Current.CancellationToken);
        Assert.True(reset.Succeeded);
        Assert.True(reset.Changed);
        Assert.True(reset.SelectionRevision > 0);
        Assert.Equal(json, File.ReadAllText(Assert.Single(reset.Diagnostics).Path));
        using var recovered = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(recovered.UsesBundled);
        Assert.Equal(reset.SelectionRevision, recovered.Revision);
        Assert.Empty(recovered.Diagnostics);
    }

    [Fact]
    public async Task RecoveryCannotResetSettingsThatAnotherWindowHasAlreadyRepaired()
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(SettingsFile);
        ChangeError(await Manager.ResetInvalidSettingsAsync(TestContext.Current.CancellationToken), "artwork-settings-valid");
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
        Assert.Empty(Directory.EnumerateFiles(Store, ".settings-backup-*"));
    }

    [Fact]
    public async Task PreferencesForOtherGamesArePreservedAndProtectedFromRemoval()
    {
        var otherGame = Pack with { ID = "other_game" };
        var first = await InstallManagedAsync();
        var other = await InstallManagedAsync(id: "example.other", game: otherGame);
        await Manager.SelectAsync(Pack, first, 0, TestContext.Current.CancellationToken);
        await Manager.SelectAsync(otherGame, other, 1, TestContext.Current.CancellationToken);
        await Manager.SelectAsync(Pack, null, 2, TestContext.Current.CancellationToken);
        using var state = await Manager.LoadSelectionAsync(otherGame, TestContext.Current.CancellationToken);
        Assert.Equal(other, state.Selected);
        Assert.False(state.UsesBundled);
        ChangeError(await Manager.RemoveAsync(other, 3, TestContext.Current.CancellationToken), "artwork-selected");
    }

    [Fact]
    public async Task CanceledValidationLeavesNoSelectionAndReleasesItsLease()
    {
        var reference = await InstallManagedAsync();
        using var cancellation = new CancellationTokenSource();
        var manager = new ArtworkPackInstaller(Store, new Decoder
        {
            Decode = _ =>
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<ArtworkImageSize?>(cancellation.Token);
        }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SelectAsync(Pack, reference, 0, cancellation.Token));
        Assert.False(File.Exists(SettingsFile));
        Assert.True((await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task SlowSelectionLoadDoesNotPublishAnObsoleteChoice()
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ArtworkPackInstaller(Store, new Decoder
        {
            Decode = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(1, 1);
        }
        });
        var pending = slow.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try { Assert.True((await Manager.SelectAsync(Pack, null, 1, TestContext.Current.CancellationToken)).Succeeded); }
        finally { release.TrySetResult(); }
        using var result = await pending;
        Assert.True(result.UsesBundled);
        Assert.Null(result.Selected);
        Assert.Equal(2, result.Revision);
        Assert.True((await Manager.RemoveAsync(reference, 2, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task ExhaustedRevisionCanBeRecoveredWithABackup()
    {
        var reference = await InstallManagedAsync();
        var json = """{"schemaVersion":1,"revision":9223372036854775807,"selections":{}}""";
        File.WriteAllText(SettingsFile, json);
        ChangeError(await Manager.SelectAsync(Pack, reference, long.MaxValue, TestContext.Current.CancellationToken), "artwork-settings-invalid");
        Assert.Equal(json, File.ReadAllText(SettingsFile));
        var reset = await Manager.ResetInvalidSettingsAsync(TestContext.Current.CancellationToken);
        Assert.True(reset.Succeeded);
        Assert.Equal(json, File.ReadAllText(Assert.Single(reset.Diagnostics).Path));
        Assert.True((await Manager.SelectAsync(Pack, reference, reset.SelectionRevision, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task OversizedSettingsAreBoundedAndRecoverableWithoutCopyingTheirContents()
    {
        await InstallManagedAsync();
        using (var file = File.Create(SettingsFile)) { file.SetLength(65 * 1024); }
        using var state = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(state.UsesBundled);
        Assert.Contains(state.Diagnostics, problem => problem.Code == "artwork-settings-invalid");
        var reset = await Manager.ResetInvalidSettingsAsync(TestContext.Current.CancellationToken);
        Assert.True(reset.Succeeded);
        Assert.Equal(65 * 1024, new FileInfo(Assert.Single(reset.Diagnostics).Path).Length);
    }

    [Fact]
    public async Task StaleLeaseFilesAreCleanedButInvalidCatalogEntriesRemainRemovable()
    {
        var reference = await InstallManagedAsync();
        var leaseDirectory = Path.Combine(Store, ".leases", reference.Id, reference.ContentDigest);
        Directory.CreateDirectory(leaseDirectory);
        File.WriteAllText(Path.Combine(leaseDirectory, "stale.lease"), "old process");
        File.WriteAllText(Path.Combine(Store, reference.Id, reference.ContentDigest, "artwork.json"), "{}");
        Assert.False(Assert.Single((await Manager.ListAsync(Pack, TestContext.Current.CancellationToken)).Entries).Usable);
        var removed = await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken);
        Assert.True(removed.Succeeded);
        Assert.Empty(Directory.EnumerateFiles(leaseDirectory));
    }

    [Fact]
    public async Task CanceledManagementOperationsLeavePreferencesAndFilesIntact()
    {
        var reference = await InstallManagedAsync();
        await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(SettingsFile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.ListAsync(Pack, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.LoadSelectionAsync(Pack, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.SelectAsync(Pack, null, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.RemoveAsync(reference, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.ResetInvalidSettingsAsync(cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(SettingsFile));
        Assert.True(Directory.Exists(Path.Combine(Store, reference.Id, reference.ContentDigest)));
    }

    [Fact]
    public async Task AFileInPlaceOfTheStoreIsReportedRatherThanTreatedAsAnEmptyStore()
    {
        File.WriteAllText(Store, "not a directory");
        using var selection = await Manager.LoadSelectionAsync(Pack, TestContext.Current.CancellationToken);
        Assert.True(selection.UsesBundled);
        Assert.Contains(selection.Diagnostics, problem => problem.Code == "artwork-path-invalid" && problem.Path == Store);
        var catalog = await Manager.ListAsync(Pack, TestContext.Current.CancellationToken);
        Assert.Contains(catalog.Diagnostics, problem => problem.Code == "artwork-path-invalid");
        Assert.Equal("not a directory", File.ReadAllText(Store));
    }

    [Fact]
    public async Task AFileInPlaceOfARevisionIsNotReportedAsSuccessfullyRemoved()
    {
        var reference = await InstallManagedAsync();
        var directory = Path.Combine(Store, reference.Id, reference.ContentDigest);
        Directory.Delete(directory, recursive: true);
        File.WriteAllText(directory, "not a revision directory");
        ChangeError(await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken), "artwork-path-invalid");
        ChangeError(await Manager.SelectAsync(Pack, reference, 0, TestContext.Current.CancellationToken), "artwork-path-invalid");
        Assert.Equal("not a revision directory", File.ReadAllText(directory));
    }

    [Fact]
    public async Task PreviewLeasePinsAnInactiveRevisionAndReadsOnlyDeclaredNotices()
    {
        var reference = await InstallManagedAsync();
        using var preview = await Manager.OpenRevisionAsync(Pack, reference, TestContext.Current.CancellationToken);
        Assert.True(preview.Succeeded);
        var notice = preview.Active!.Pack.Manifest.Credits[0];
        Assert.Contains("MIT", await preview.Active.ReadNoticeAsync(notice.LicenseFile, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => preview.Active.ReadNoticeAsync("../outside.txt", TestContext.Current.CancellationToken));
        ChangeError(await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken), "artwork-in-use");
        preview.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => preview.Active.ReadNoticeAsync(notice.LicenseFile, TestContext.Current.CancellationToken));
        Assert.True((await Manager.RemoveAsync(reference, 0, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task NoticeReadsRemainBoundedAfterLocalTampering()
    {
        var reference = await InstallManagedAsync();
        using var preview = await Manager.OpenRevisionAsync(Pack, reference, TestContext.Current.CancellationToken);
        var credit = preview.Active!.Pack.Manifest.Credits[0];
        using (var file = File.Create(Path.Combine(preview.Active.Pack.Directory, credit.LicenseFile))) { file.SetLength(ArtworkLimits.LicenseBytes + 1); }
        await Assert.ThrowsAsync<InvalidDataException>(() => preview.Active.ReadNoticeAsync(credit.LicenseFile, TestContext.Current.CancellationToken));
    }
}
