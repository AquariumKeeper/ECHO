using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>
/// The C4 slice, headless: equip, roll, talent, assumption, stats and their details, undo, and library save and
/// open, driven through the editor's view models as the window will drive them, on the real Division pack.
/// </summary>
public sealed class EditorViewModelTests : IDisposable
{
    private static CompiledPackExpressions Division => TestPack.Division;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "editor-library-" + Guid.NewGuid().ToString("N"));
    private readonly PlannerSession session;
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public EditorViewModelTests()
    {
        // Typed numbers are read in the current culture; pin one so the test inputs mean what they say.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(Division, PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    /// <summary>Waits for the latest stats and shows them, as the UI thread would when the result is posted.</summary>
    private async Task Resolved()
    {
        await editor.Resolution;
        dispatcher.RunPending();
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private BuildSnapshot Build => session.Snapshot.State.Build;

    [Fact]
    public void TheEditorFollowsThePackLayout()
    {
        Assert.Equal(["Weapons", "Gear"], editor.SlotGroups.Select(group => group.Name));
        Assert.Equal(["primary_weapon", "secondary_weapon", "sidearm"], editor.SlotGroups[0].Slots.Select(slot => slot.Id));
        Assert.All(editor.SlotGroups.SelectMany(group => group.Slots), slot => Assert.False(slot.IsEquipped));
        Assert.Contains(editor.Headline, figure => figure.Label == "Build DPS");
        Assert.Equal(3, editor.WeaponTabs.Length);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void AnAttributeRolledInOneSlotIsNotOfferedInTheItemsOtherSlots()
    {
        // R40: an item's rolled attributes are distinct.
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var (first, second) = (mask.Attributes.Single(attribute => attribute.Id == "minor_1"), mask.Attributes.Single(attribute => attribute.Id == "minor_2"));
        Assert.Contains(second.Choices, choice => choice.Id == "weapon_handling_gear_minor");
        first.Selected = Pick(first.Choices, "weapon_handling_gear_minor");
        Assert.Equal("weapon_handling_gear_minor", Build.AttributeRolls["mask"]["minor_1"].AttributeId);
        Assert.DoesNotContain(second.Choices, choice => choice.Id == "weapon_handling_gear_minor");
        Assert.Equal("weapon_handling_gear_minor", first.Selected.Id);
        // Clearing it offers it again.
        first.Selected = Pick(first.Choices, "");
        Assert.Contains(second.Choices, choice => choice.Id == "weapon_handling_gear_minor");

        // A saved build may still repeat it (the frozen reference builds do); both slots keep showing their roll.
        var saved = BuildSnapshot.Create(Division.Pack).WithEquipment("mask", "mask_n_5_11_tactical_mask")
            .WithAttributeRoll("mask", "minor_1", new("weapon_handling_gear_minor", 0.08)).WithAttributeRoll("mask", "minor_2", new("weapon_handling_gear_minor", 0.08));
        var repeated = new EditorViewModel(new PlannerSession(Division, PlannerState.Of(saved), new OperationApplier(Division)), new FileBuildLibrary(folder), dispatcher);
        Assert.All(repeated.Slot("mask").Attributes.Where(attribute => attribute.Id.StartsWith("minor_", StringComparison.Ordinal)),
            attribute => Assert.Equal(("weapon_handling_gear_minor", false), (attribute.Selected.Id, attribute.Selected.IsUnknown)));
    }

    [Fact]
    public async Task EquippingRollingAndStatsUpdateThroughTheSession()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        Assert.Equal("mask_n_5_11_tactical_mask", Build.Equipment["mask"]);
        Assert.True(editor.CanUndo);
        Assert.Null(editor.Message);

        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = Pick(core.Choices, "armor_gear_core");
        Assert.True(core.HasRoll);
        var highest = Build.AttributeRolls["mask"]["core_1"].Value;

        // Typed text is a draft until committed; a bad draft is reported and changes nothing.
        core.ValueText = "lots";
        core.CommitValue();
        Assert.Equal("\"lots\" isn't a number", editor.Message);
        Assert.Equal(highest, Build.AttributeRolls["mask"]["core_1"].Value);

        // An out-of-range roll is refused by the applier, and the box returns to the stored value.
        core.ValueText = "999999999";
        core.CommitValue();
        Assert.NotNull(editor.Message);
        Assert.Equal(Formatting.Input(StatFormats.Integer, highest), core.ValueText);

        core.ValueText = "100000";
        core.CommitValue();
        Assert.Null(editor.Message);
        Assert.Equal(100000, Build.AttributeRolls["mask"]["core_1"].Value);

        await Resolved();
        var expected = BuildResolver.Resolve(Division, Build, session.Snapshot.State.Assumptions).Calculation.Stats;
        var armor = editor.Summary.SelectMany(group => group.Rows).First(row => row.Id == "character_armor");
        Assert.Equal(Formatting.Value(armor.Format, expected["character_armor"]), armor.Value);

        // Character armor is a formula over other stats; its breakdown, as Go's, reads the formula in names and each input's
        // value, the rolled armor among them.
        editor.SelectedStat = armor;
        var rolled = Division.Pack.Stats.Single(stat => stat.ID == "armor");
        Assert.Contains($"Formula: (Equipped Base Armor + {rolled.Name}) * (1 + Total Armor)", armor.Details!.Lines);
        Assert.Contains($"{rolled.Name}: {Formatting.Value(rolled.Format, expected["armor"])}", armor.Details.Lines);
        Assert.Equal($"Final: {armor.Value}", armor.Details.Lines[^1]);
        Assert.Equal(100000, expected["armor"]);
    }

    [Fact]
    public async Task TalentsAssumptionsAndStatDetails()
    {
        var primary = editor.Slot("primary_weapon");
        primary.Equip("primary_weapon_acr");
        var talent = primary.Talents.First(slot => slot.Choices.Length > 2);
        var second = talent.Choices[2];
        talent.Selected = second;
        Assert.Equal(second.Id, Build.TalentSelections["primary_weapon"][talent.Id]);

        editor.WeaponTabs.Single(tab => tab.SlotId == "primary_weapon").ToggleCommand.Execute(null);
        Assert.Equal("primary_weapon", session.Snapshot.State.Assumptions[GamePack.WeaponSlotSelector]);

        await Resolved();
        var damage = editor.Summary.SelectMany(group => group.Rows).First(row => row.Id == "equipped_weapon_damage");
        Assert.NotEqual("–", damage.Value);
        // A weapon stat's breakdown names the weapon slot, and ends with the final value.
        editor.SelectedStat = damage;
        Assert.NotNull(damage.Details);
        Assert.Equal(damage.Name, damage.Details.Name);
        Assert.Contains("Weapon Slot: Primary Weapon", damage.Details.Lines);
        Assert.Equal($"Final: {damage.Value}", damage.Details.Lines[^1]);
    }

    [Fact]
    public void UndoAndRedoMoveThroughTheEdits()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        editor.UndoCommand.Execute(null);
        Assert.Equal("", mask.ItemId);
        Assert.False(Build.Equipment.TryGetValue("mask", out var item) && item.Length > 0);
        Assert.True(editor.CanRedo);
        editor.RedoCommand.Execute(null);
        Assert.Equal("mask_n_5_11_tactical_mask", mask.ItemId);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void RenamingAppliesTheTypedName()
    {
        editor.BuildName = "  ";
        editor.RenameCommand.Execute(null);
        Assert.Equal("The build name must not be blank", editor.Message);
        editor.BuildName = "Striker";
        editor.RenameCommand.Execute(null);
        Assert.Equal("Striker", Build.Meta.Name);
    }

    [Fact]
    public async Task SavingOpeningAndConflictsGoThroughTheLibrary()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        Assert.True(editor.IsDirty);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(editor.RecordId);
        Assert.False(editor.IsDirty);
        Assert.Single(editor.LibraryEntries);

        // A second save replaces the same record.
        editor.WeaponTabs.Single(tab => tab.SlotId == "primary_weapon").ToggleCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Single(editor.LibraryEntries);

        // Someone else changes the saved build; saving over it is a conflict, not a lost edit.
        var other = new FileBuildLibrary(folder);
        var saved = await other.LoadAsync(editor.RecordId!, TestContext.Current.CancellationToken);
        await other.ReplaceAsync(saved.Id, saved.Build with { Meta = new("Theirs", "") }, saved.Version, TestContext.Current.CancellationToken);
        mask.UnequipCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("changed since it was opened", editor.Message, StringComparison.Ordinal);
        await editor.SaveAsNewCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.LibraryEntries.Length);

        // A new build, then opening the first record again.
        await editor.NewBuildCommand.ExecuteAsync(null);
        Assert.Equal("", mask.ItemId);
        Assert.Null(editor.RecordId);
        await editor.OpenCommand.ExecuteAsync(editor.LibraryEntries.Single(entry => entry.Name == "Theirs"));
        Assert.Equal("mask_n_5_11_tactical_mask", mask.ItemId);
        Assert.Equal("Theirs", editor.BuildName);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task FirstSaveOfAMatchingNameRequiresExplicitReplacement(bool? decision)
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = " raid ";
        editor.RenameCommand.Execute(null);
        editor.Slot("mask").Equip("mask_n_5_11_tactical_mask");
        var host = decision is { } answer ? new TestHost { Confirms = answer } : null;
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        var stored = await store.LoadAsync(original.Id, TestContext.Current.CancellationToken);
        if (decision == true)
        {
            Assert.Equal(original.Id, editor.RecordId);
            Assert.Equal("mask_n_5_11_tactical_mask", stored.Build.Equipment["mask"]);
            Assert.False(editor.IsDirty);
        }
        else
        {
            Assert.Null(editor.RecordId);
            Assert.Equal(original.Version, stored.Version);
            Assert.True(editor.IsDirty);
        }
        if (host is not null) { Assert.Contains("Replace it", Assert.Single(host.Questions), StringComparison.Ordinal); }
        else
        {
            Assert.Contains("requires confirmation", editor.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Open it", editor.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SaveAsNewDeliberatelyKeepsAnotherBuildWithTheSameName()
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost();
        editor.Host = host;
        await editor.SaveAsNewCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.LibraryEntries.Length);
        Assert.NotEqual(original.Id, editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Equal(original.Version, (await store.LoadAsync(original.Id, TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task AmbiguousNamesNeverChooseAnArbitraryReplacement()
    {
        var store = new FileBuildLibrary(folder);
        await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.LibraryEntries.Length);
        Assert.Null(editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Contains("More than one", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitLibrarySelectionResolvesDuplicateNamesWithoutOpeningTheTarget()
    {
        var store = new FileBuildLibrary(folder);
        var first = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        var second = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        editor.Slot("mask").Equip("mask_n_5_11_tactical_mask");
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.RecordId);
        editor.SelectedSavedBuild = editor.SavedBuilds.Single(saved => saved.Entry.Id == second.Id);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(second.Id, editor.RecordId);
        Assert.Equal("mask_n_5_11_tactical_mask", (await store.LoadAsync(second.Id, TestContext.Current.CancellationToken)).Build.Equipment["mask"]);
        Assert.Equal(first.Version, (await store.LoadAsync(first.Id, TestContext.Current.CancellationToken)).Version);
        Assert.Contains("you just selected", Assert.Single(host.Questions), StringComparison.Ordinal);
        Assert.False(editor.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteDuringConfirmationCannotBeOverwrittenByTheApprovedOldVersion(bool deleted)
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        editor.Host = new TestHost(async () =>
        {
            if (deleted) { await store.DeleteAsync(original.Id, original.Version, TestContext.Current.CancellationToken); }
            else { await store.ReplaceAsync(original.Id, original.Build with { Meta = new("Raid", "Other writer") }, original.Version, TestContext.Current.CancellationToken); }
            return true;
        });
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.RecordId);
        Assert.True(editor.IsDirty);
        Assert.Contains("changed while replacement was being confirmed", editor.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Id, editor.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("open the saved", editor.Message, StringComparison.Ordinal);
        if (deleted) { Assert.Empty(await store.ListAsync(TestContext.Current.CancellationToken)); }
        else { Assert.Equal("Other writer", (await store.LoadAsync(original.Id, TestContext.Current.CancellationToken)).Build.Meta.Notes); }
    }

    [Fact]
    public async Task EditsDuringConfirmationRemainUnsavedAfterTheCapturedBuildIsWritten()
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        editor.Host = new TestHost(() =>
        {
            editor.BuildName = "Later edit";
            editor.RenameCommand.Execute(null);
            return Task.FromResult(true);
        });
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(original.Id, editor.RecordId);
        Assert.Equal("Raid", (await store.LoadAsync(original.Id, TestContext.Current.CancellationToken)).Build.Meta.Name);
        Assert.Equal("Later edit", Build.Meta.Name);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task AnAlreadySavedBuildContinuesToSaveByItsIdentity()
    {
        editor.BuildName = "Original";
        editor.RenameCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        var id = editor.RecordId;
        var store = new FileBuildLibrary(folder);
        var other = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost();
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(id, editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Equal(other.Version, (await store.LoadAsync(other.Id, TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task ANameInAnotherPackIsNotAReplacementTarget()
    {
        var store = new FileBuildLibrary(folder);
        var other = await store.CreateAsync(Build with { PackId = "another_pack", Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost();
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(editor.RecordId);
        Assert.NotEqual(other.Id, editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Equal(2, editor.LibraryEntries.Length);
    }

    [Fact]
    public async Task RealCorruptRecordsBlockNameMatchingAndStayIntactWhenAnExplicitCopyIsCreated()
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        var path = Path.Combine(folder, original.Id + FileBuildLibrary.Extension);
        await File.WriteAllTextAsync(path, "broken data", TestContext.Current.CancellationToken);
        var damaged = Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(damaged.Name);
        Assert.Empty(damaged.PackId);
        Assert.NotNull(damaged.Problem);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Contains("unreadable records", editor.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", editor.Message, StringComparison.Ordinal);
        Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        await editor.SaveAsNewCommand.ExecuteAsync(null);
        Assert.NotNull(editor.RecordId);
        Assert.Equal(2, editor.LibraryEntries.Length);
        Assert.Equal("broken data", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnOldPickerSelectionIsClearedBeforeResolvingDuplicateNames()
    {
        var store = new FileBuildLibrary(folder);
        await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        var second = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        await editor.RefreshLibraryCommand.ExecuteAsync(null);
        editor.SelectedSavedBuild = editor.SavedBuilds.Single(saved => saved.Entry.Id == second.Id);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.SelectedSavedBuild);
        Assert.Null(editor.RecordId);
        Assert.Empty(host.Questions);
        Assert.Contains("More than one", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherLibraryActionExpiresThePendingDuplicateChoice()
    {
        var store = new FileBuildLibrary(folder);
        await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        var second = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "Raid";
        editor.RenameCommand.Execute(null);
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        editor.SelectedSavedBuild = editor.SavedBuilds.Single(saved => saved.Entry.Id == second.Id);
        await editor.RefreshLibraryCommand.ExecuteAsync(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.SelectedSavedBuild);
        Assert.Null(editor.RecordId);
        Assert.Empty(host.Questions);
    }

    [Fact]
    public async Task ReplacementConfirmationDisclosesTheExactNewName()
    {
        var store = new FileBuildLibrary(folder);
        var original = await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken);
        editor.BuildName = "RAID";
        editor.RenameCommand.Execute(null);
        var host = new TestHost { Confirms = true };
        editor.Host = host;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("name will change from \"Raid\" to \"RAID\"", Assert.Single(host.Questions), StringComparison.Ordinal);
        Assert.Equal("RAID", (await store.LoadAsync(original.Id, TestContext.Current.CancellationToken)).Build.Meta.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstNamedSaveListsOnceAndUpdatesTheListedVersionFromItsWrite(bool replaces)
    {
        var store = new FileBuildLibrary(folder);
        if (replaces) { await store.CreateAsync(Build with { Meta = new("Raid") }, TestContext.Current.CancellationToken); }
        var counted = new CountingLibrary(store);
        using var model = new EditorViewModel(session, counted, dispatcher);
        model.BuildName = "Raid";
        model.RenameCommand.Execute(null);
        model.Slot("mask").Equip("mask_n_5_11_tactical_mask");
        model.Host = new TestHost { Confirms = true };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, counted.ListCalls);
        var entry = Assert.Single(model.LibraryEntries);
        var actual = await store.LoadAsync(entry.Id, TestContext.Current.CancellationToken);
        Assert.Equal(actual.Version, entry.Version);
        Assert.Equal(actual.Build.Meta.Name, entry.Name);
        Assert.Equal(actual.SavedAt, entry.SavedAt);
        Assert.False(model.IsDirty);
    }

    private sealed class CountingLibrary(IBuildLibrary inner) : IBuildLibrary
    {
        public int ListCalls { get; private set; }
        public Task<System.Collections.Immutable.ImmutableArray<LibraryEntry>> ListAsync(CancellationToken token = default)
        { ListCalls++; return inner.ListAsync(token); }
        public Task<LibraryBuild> LoadAsync(string id, CancellationToken token = default) => inner.LoadAsync(id, token);
        public Task<LibraryBuild> CreateAsync(BuildSnapshot build, CancellationToken token = default) => inner.CreateAsync(build, token);
        public Task<LibraryBuild> ReplaceAsync(string id, BuildSnapshot build, string version, CancellationToken token = default) => inner.ReplaceAsync(id, build, version, token);
        public Task DeleteAsync(string id, string version, CancellationToken token = default) => inner.DeleteAsync(id, version, token);
    }

    /// <summary>Only the latest revision's stats are ever shown, however many edits are in flight.</summary>
    [Fact]
    public async Task OnlyTheLatestStatsAreShown()
    {
        var mask = editor.Slot("mask");
        foreach (var id in TestPack.Division.Pack.Items.Where(item => item.Slot == "mask").Take(8).Select(item => item.ID)) { mask.Equip(id); }
        await Resolved();
        var expected = BuildResolver.Resolve(Division, Build, session.Snapshot.State.Assumptions).Calculation.Stats;
        Assert.All(editor.Summary.SelectMany(group => group.Rows).Where(row => row.IsVisible),
            row => Assert.Equal(expected.TryGetValue(row.Id, out var value) ? Formatting.Value(row.Format, value) : "–", row.Value));
    }

    /// <summary>A number being typed survives an edit elsewhere; a refused one returns to the stored value.</summary>
    [Fact]
    public void ADraftSurvivesOtherEdits()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = Pick(core.Choices, "armor_gear_core");
        core.ValueText = "120000";
        editor.WeaponTabs.Single(tab => tab.SlotId == "primary_weapon").ToggleCommand.Execute(null);
        Assert.Equal("120000", core.ValueText);
        editor.BuildName = "Draft name";
        editor.UndoCommand.Execute(null);
        Assert.Equal("Draft name", editor.BuildName);
    }

    /// <summary>Saving (Ctrl+S included, which doesn't move focus) commits a roll still being typed.</summary>
    [Fact]
    public async Task SavingCommitsARollBeingTyped()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = Pick(core.Choices, "armor_gear_core");
        core.ValueText = "120000";
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = await new FileBuildLibrary(folder).LoadAsync(editor.RecordId!, TestContext.Current.CancellationToken);
        Assert.Equal(120000, saved.Build.AttributeRolls["mask"]["core_1"].Value);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void TypedNumbersMustNotBeMisread()
    {
        Assert.Null(Formatting.Parse(StatFormats.Percent, "1,5"));
        Assert.Equal(0.015, Formatting.Parse(StatFormats.Percent, "1.5")!.Value, 12);
        Assert.Equal(0.015, Formatting.Parse(StatFormats.Percent, "1.5 %")!.Value, 12);
        Assert.Null(Formatting.Parse(StatFormats.Integer, "lots"));
    }

    /// <summary>Unsaved changes are differences in the build, so stepping back to the saved state isn't one.</summary>
    [Fact]
    public async Task UnsavedChangesFollowTheBuildNotTheHistory()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        await editor.SaveCommand.ExecuteAsync(null);
        mask.UnequipCommand.Execute(null);
        Assert.True(editor.IsDirty);
        editor.UndoCommand.Execute(null);
        Assert.False(editor.IsDirty);
        editor.RedoCommand.Execute(null);
        editor.UndoCommand.Execute(null);
        Assert.False(editor.IsDirty);
    }

    /// <summary>New and Open never throw away unsaved changes without the window's confirmation.</summary>
    [Fact]
    public async Task UnsavedChangesNeedConfirmationToDiscard()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        await editor.NewBuildCommand.ExecuteAsync(null);
        Assert.Equal("mask_n_5_11_tactical_mask", Build.Equipment["mask"]);
        Assert.Contains("unsaved changes", editor.Message, StringComparison.Ordinal);

        var asked = 0;
        editor.Host = new TestHost(() => { asked++; return Task.FromResult(false); });
        await editor.NewBuildCommand.ExecuteAsync(null);
        Assert.Equal((1, "mask_n_5_11_tactical_mask"), (asked, Build.Equipment["mask"]));

        editor.Host = new TestHost(() => Task.FromResult(true));
        await editor.NewBuildCommand.ExecuteAsync(null);
        Assert.Equal("", mask.ItemId);
        Assert.False(editor.IsDirty);
    }

    /// <summary>An id the pack no longer has is shown in its picker, not silently replaced.</summary>
    [Fact]
    public async Task IdsThePackNoLongerHasStayVisible()
    {
        var rifle = editor.Slot("primary_weapon");
        rifle.Equip("primary_weapon_acr");
        var talentSlot = rifle.Talents.First().Id;
        var build = Build with { TalentSelections = Build.TalentSelections.SetItem("primary_weapon", System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(talentSlot, "retired_talent")) };
        await new FileBuildLibrary(folder).CreateAsync(build, TestContext.Current.CancellationToken);
        editor.Host = new TestHost(() => Task.FromResult(true));
        await editor.RefreshLibraryCommand.ExecuteAsync(null);
        await editor.OpenCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Null(editor.Message);
        var talent = editor.Slot("primary_weapon").Talents.First(slot => slot.Id == talentSlot);
        Assert.Equal("retired_talent", talent.Selected.Id);
        Assert.Contains(talent.Selected, talent.Choices);
    }
}
