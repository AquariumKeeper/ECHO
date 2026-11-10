using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The inspector's last Go details: Compare talents, the candidate's item card, picker rows' dots, and inspector art.</summary>
public sealed class InspectorDetailTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "inspector-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public InspectorDetailTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher,
            new PackArtwork(TestPack.Division.Pack.Presentation, Path.Combine(AppContext.BaseDirectory, "packs", "division-2")));
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task CompareTalentsShowsWhatEachWouldChangeAndChooseApplies()
    {
        var primary = editor.Slot("primary_weapon");
        primary.Equip("primary_weapon_acr");
        editor.WeaponTabs[0].ToggleCommand.Execute(null);
        // Talents act under their assumptions; with Killer's switched on, choosing it changes the build.
        Assert.True(session.Apply([new ContextOperation("killer_active", Text: "true")], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        var talent = primary.Talents.Single();
        TalentCompareViewModel? compare = null;
        editor.Host = new TestHost { Compared = opened => compare = opened };
        talent.CompareCommand.Execute(null);
        Assert.NotNull(compare);
        Assert.Equal("Compare talents", compare.Title);
        await compare.LoadAsync();
        dispatcher.RunPending();

        Assert.Equal(talent.Choices.Length - 1, compare.Rows.Length);
        Assert.Equal($"{compare.Rows.Length} of {compare.Rows.Length} talents", compare.Shown);
        Assert.StartsWith("Compared with the current talent: +", compare.Rows.Single(row => row.Id == "weapon_talent_killer").Effect, StringComparison.Ordinal);
        Assert.Equal("No change to modeled stats with the current view assumptions.", compare.Rows.Single(row => row.Id == "weapon_talent_eyeless").Effect);
        // Every previewed choice describes its own modeled channels, never the current talent's or "unavailable".
        Assert.Contains("Conditional on", compare.Rows.Single(row => row.Id == "weapon_talent_killer").Coverage, StringComparison.Ordinal);
        Assert.All(compare.Rows, row => Assert.NotEqual(TalentCompareViewModel.CoverageUnavailable, row.Coverage));

        // Only talents that change stats, and Affects, which also sorts by that stat's change.
        compare.ChangedOnly = true;
        Assert.All(compare.Rows, row => Assert.NotEmpty(row.Deltas));
        compare.Affected = compare.Affects[1];
        var stat = compare.Affected.Id;
        Assert.All(compare.Rows, row => Assert.True(row.Deltas.ContainsKey(stat)));
        var deltas = compare.Rows.Select(row => row.Deltas[stat]).ToList();
        Assert.Equal(deltas.OrderByDescending(delta => delta), deltas);

        // Choose applies it and closes.
        var chosen = false;
        compare.Chosen += (_, _) => chosen = true;
        var pick = compare.Rows[0];
        pick.ChooseCommand.Execute(null);
        Assert.True(chosen);
        Assert.Equal(pick.Id, session.Snapshot.State.Build.TalentSelections["primary_weapon"][talent.Id]);
    }

    [Fact]
    public async Task TheCandidateHasGosItemCard()
    {
        await editor.Resolution;
        dispatcher.RunPending();
        editor.Slot("backpack").BrowseCommand.Execute(null);
        var picker = editor.Picker!;
        await picker.Comparison;
        dispatcher.RunPending();
        picker.Highlighted = picker.Rows.Single(row => row.Id == "backpack_devil_s_due");
        await editor.PreviewResolution;
        dispatcher.RunPending();

        var card = Assert.IsType<CandidateViewModel>(editor.Inspected).Card;
        Assert.Contains(card, line => line.IsHeader && line.Text == "Talents");
        Assert.Contains(card, line => line.IsTalent);
        Assert.Contains(card, line => line.IsText && line.Text.Contains("— choose from", StringComparison.Ordinal));
        Assert.All(card.Where(line => line.IsRoll), line => Assert.InRange(line.Fraction, 0, 1));
    }

    [Theory]
    [InlineData("gear_talent_resourceful", "set-piece credit")]
    [InlineData("gear_talent_abridged", "No eligible source")]
    [InlineData("gear_talent_ablative_nano_plating", "Party projection")]
    [InlineData("gear_talent_resolved", "Pack combat rules exist")]
    [InlineData("gear_talent_perfect_tag_team", "Pack combat rules exist")]
    [InlineData("gear_talent_perfect_calculated", "Pack combat rules exist")]
    [InlineData("gear_talent_bewildered", "No modeled effect")]
    public async Task CandidateCardsDescribeIndirectAndMissingModels(string talentId, string expected)
    {
        var item = TestPack.Division.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talentId)));
        await editor.Resolution;
        dispatcher.RunPending();
        editor.Slot(item.Slot).BrowseCommand.Execute(null);
        var picker = editor.Picker!;
        await picker.Comparison;
        dispatcher.RunPending();
        picker.Highlighted = picker.Rows.Single(row => row.Id == item.ID);
        await editor.PreviewResolution;
        dispatcher.RunPending();
        var card = Assert.IsType<CandidateViewModel>(editor.Inspected).Card;
        Assert.Contains(card, line => line.Text.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(card, line => line.Text.Contains("Description only;", StringComparison.Ordinal));
        Assert.Contains("fixed assumptions", picker.Coverage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickerRowsShowTheAttributesAnItemAlwaysHas()
    {
        editor.Slot("mask").BrowseCommand.Execute(null);
        var picker = editor.Picker!;
        await picker.Comparison;
        dispatcher.RunPending();
        // Some items have fixed attributes (an exotic's, a named item's); their rows show their dots.
        Assert.Contains(picker.Rows, row => !row.Dots.IsEmpty);
        Assert.All(picker.Rows, row => Assert.All(row.Dots, dot => Assert.StartsWith("#", dot, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheInspectorLeavesUndeliveredArtToTheAppsSymbols()
    {
        // The pack registers only original slot art (R34): specialization, node and skill art is not delivered, so
        // the views draw the app's own symbols for them.
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "sharpshooter");
        Assert.Equal("Sharpshooter", editor.Specialization.Title);
        Assert.Null(editor.Specialization.Icon);
        Assert.False(editor.Specialization.HasImage);
        Assert.All(editor.Specialization.Nodes, node => Assert.Null(node.Icon));

        var skill = editor.SkillSlots[0];
        skill.Selected = skill.Choices.Single(choice => choice.Id == "skill_assault_turret");
        Assert.Null(skill.Icon);
    }

    [Fact]
    public void EveryRollAndRankHasMaxAndASlider()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = core.Choices.Single(choice => choice.Id == "armor_gear_core");
        core.ValueText = "100000";
        Assert.True(core.CommitValue());
        Assert.True(core.MaximumCommand.CanExecute(null));
        core.MaximumCommand.Execute(null);
        Assert.Equal(core.SliderMaximum, session.Snapshot.State.Build.AttributeRolls["mask"]["core_1"].Value);

        // A rank slides in whole steps, a drag one undo step, and Max sets its highest.
        var upgrade = mask.Upgrade!;
        upgrade.SliderValue = 7.4;
        upgrade.SliderValue = 12.6;
        Assert.Equal(13, session.Snapshot.State.Build.EquipmentUpgrades["mask"].Rank);
        upgrade.MaximumCommand.Execute(null);
        Assert.Equal(upgrade.Max, session.Snapshot.State.Build.EquipmentUpgrades["mask"].Rank);

        // A skill's mod can't be changed while the skill isn't accepted.
        var skill = editor.SkillSlots[0];
        skill.Selected = skill.Choices.Single(choice => choice.Id == "skill_assault_turret");
        var mod = skill.Mods[0];
        Assert.False(mod.IsEnabled);
    }
}
