using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The Party tab (F44), as the Go app's TestG395PartyTab: members from a code, a saved build or by hand, buffs off until ticked.</summary>
public sealed class PartyViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "party-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly FileBuildLibrary library;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public PartyViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        library = new FileBuildLibrary(folder);
        editor = new(session, library, dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private BuildSnapshot Build => session.Snapshot.State.Build;
    private PartyViewModel Party => editor.Party;
    private PartyMemberViewModel Member(string name) => Party.Members.Single(member => member.Name == name);

    /// <summary>A giver named <paramref name="name"/> wearing an item whose talent gives ally effect <paramref name="effectId"/>.</summary>
    internal static BuildSnapshot Giver(string name, string effectId = "vanguard")
    {
        var pack = TestPack.Division.Pack;
        var talent = pack.Talents.First(talent => talent.AllyEffects.Any(effect => effect.ID == effectId));
        var item = pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talent.ID)));
        var slot = item.TalentSlots.First(slot => slot.Choices.Contains(talent.ID));
        var applier = new OperationApplier(TestPack.Division);
        var outcome = applier.Apply(PlannerState.Of(BuildSnapshot.Create(pack)),
            [new NameOperation(name), new EquipmentOperation(item.Slot, item.ID), new TalentOperation(item.Slot, slot.ID, talent.ID)]);
        Assert.True(outcome.Succeeded);
        return outcome.State.Build;
    }

    [Fact]
    public void MembersFromACodeAndByHandOfferBuffsOffUntilTickedAndTheStrongestApplies()
    {
        Assert.True(Party.IsEmpty);
        Assert.True(Party.CanAdd);

        // A code member is named after the giver's build by default, and its buffs start off.
        Assert.Null(Party.AddFromCode(" ", ShareCode.Encode(Giver("Alex"))));
        var alex = Member("Alex");
        Assert.False(Party.IsEmpty);
        Assert.Equal((PartyMemberViewModel.FromCode, false, true), (alex.Source, alex.IsHandEntered, alex.CanReread));
        var vanguard = Assert.Single(alex.Effects);
        Assert.StartsWith("Vanguard: +", vanguard.Label, StringComparison.Ordinal);
        Assert.EndsWith(" Party Bonus Armor", vanguard.Label, StringComparison.Ordinal);
        Assert.False(vanguard.IsActive);
        Assert.NotEmpty(vanguard.Description);

        // Ticking applies it in one undo step.
        var before = session.Snapshot.Revision;
        vanguard.IsActive = true;
        Assert.Equal(before + 1, session.Snapshot.Revision);
        Assert.True(Build.Party[0].Effects[0].Active);
        Assert.Same(vanguard, Member("Alex").Effects.Single());
        Assert.False(vanguard.IsOutranked);

        // By hand: the armor, then a buff from the chooser, which is then no longer offered.
        Assert.Null(Party.AddByHand("Sam", "100,000,000"));
        var sam = Member("Sam");
        Assert.Equal((PartyMemberViewModel.ByHand, true, false), (sam.Source, sam.IsHandEntered, sam.CanReread));
        Assert.Equal("100000000", sam.Armor.ValueText);
        Assert.Equal(sam.BuffChoices.Select(choice => choice.Name).Order(StringComparer.Ordinal), sam.BuffChoices.Select(choice => choice.Name));
        sam.SelectedBuff = sam.BuffChoices.Single(choice => choice.Name == "Perfect Vanguard");
        Assert.Null(sam.SelectedBuff);
        Assert.DoesNotContain(sam.BuffChoices, choice => choice.Name == "Perfect Vanguard");
        var perfect = Assert.Single(sam.Effects);
        Assert.True(perfect.IsActive);
        Assert.Equal(0.5 * 1e8, Build.Party[1].Effects[0].Values["party_bonus_armor"]);
        Assert.True(sam.CanReread);

        // The same kind of buff applies once, the strongest: Alex's is outranked.
        Assert.True(vanguard.IsOutranked);
        Assert.False(perfect.IsOutranked);

        // A changed armor re-values the buff.
        sam.Armor.ValueText = "2";
        Assert.True(sam.Armor.CommitValue());
        Assert.Equal(1, Build.Party[1].Effects[0].Values["party_bonus_armor"]);
        Assert.True(perfect.IsOutranked);
        Assert.False(vanguard.IsOutranked);

        // Full at three.
        Assert.Null(Party.AddByHand("Kim", ""));
        Assert.False(Party.CanAdd);

        Member("Sam").RemoveCommand.Execute(null);
        Assert.Equal(["Alex", "Kim"], Build.Party.Select(member => member.Name));
        Assert.True(Party.CanAdd);
        editor.UndoCommand.Execute(null);
        Assert.Equal(["Alex", "Sam", "Kim"], Party.Members.Select(member => member.Name));
    }

    [Fact]
    public void AddingReportsWhyNotInlineAndLeavesTheEditorsMessageAlone()
    {
        Assert.False(string.IsNullOrEmpty(Party.AddFromCode("", "not a real share code!!")));
        Assert.Contains("share code can't be read", Party.AddFromCode("Bad", "not a real share code!!"), StringComparison.Ordinal);
        Assert.Equal("a party member needs a name", Party.AddByHand(" ", "5"));
        Assert.Equal("giver armor must be a number", Party.AddByHand("Sam", "lots"));
        Assert.Null(Party.AddByHand("Sam", ""));
        // The operation would update a member of the same name; adding refuses it instead.
        Assert.Equal("party member \"sam\" is listed twice", Party.AddByHand("sam", "5"));
        Assert.Equal(0, Build.Party.Single().Armor);
        Assert.Null(editor.Message);

        // A typed armor that isn't a number is reported where edits are.
        var sam = Member("Sam");
        sam.Armor.ValueText = "x";
        Assert.False(sam.Armor.CommitValue());
        Assert.Equal("giver armor must be a number", editor.Message);
    }

    [Fact]
    public async Task AMemberFromALibraryBuildIsAddedAsItsShareCode()
    {
        Assert.Equal("choose a saved build", await Party.AddFromLibraryAsync(null, "Anyone"));
        var saved = await library.CreateAsync(Giver("Morgan"), TestContext.Current.CancellationToken);
        await library.CreateAsync(Giver("Morgan", "overwatch"), TestContext.Current.CancellationToken);
        var choices = await Party.LibraryChoicesAsync();
        Assert.Equal(2, choices.Length);
        Assert.Contains(choices, choice => choice.Name == $"Morgan ({choice.Id})");

        Assert.Null(await Party.AddFromLibraryAsync(choices.Single(choice => choice.Id == saved.Id), ""));
        var member = Build.Party.Single();
        Assert.Equal("Morgan", member.Name);
        Assert.Equal(ShareCode.Encode(saved.Build with { Party = [] }), member.Code);
        Assert.Equal("vanguard", member.Effects.Single().Id);
    }

    [Fact]
    public void ReReadKeepsTicksAndAnEffectTheDataNoLongerHasIsAWarning()
    {
        var stale = BuildSnapshot.Create(TestPack.Division.Pack).WithParty([new("Old", "", 1000, [new("retired_effect", System.Collections.Immutable.ImmutableDictionary<string, double>.Empty, true)])]);
        session.Reset(PlannerState.Of(stale), session.Snapshot.Revision);
        dispatcher.RunPending();
        var effect = Member("Old").Effects.Single();
        Assert.True(effect.IsStale);
        Assert.Equal("retired_effect: no longer in the game data; Re-read to update.", effect.Warning);

        Member("Old").RereadCommand.Execute(null);
        Assert.Empty(Build.Party.Single().Effects);
        Assert.Empty(Member("Old").Effects);
    }
}
