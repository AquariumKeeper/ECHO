using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The skill slots (F20, F22, F23): choosing a skill, its Expertise grade, its mods and rolls, and its preview.</summary>
public sealed class SkillViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "skills-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public SkillViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private async Task Resolved()
    {
        await editor.Resolution;
        dispatcher.RunPending();
    }

    private SkillSlotViewModel Skill(string id) => editor.SkillSlots.Single(slot => slot.Id == id);
    private BuildSnapshot Build => session.Snapshot.State.Build;
    private string Output(SkillSlotViewModel slot, string name) => slot.Outputs.Single(output => output.Name == name).Value;

    [Fact]
    public async Task ASkillShowsItsPreviewAndExpertiseRaisesIt()
    {
        await Resolved();
        var first = Skill("skill_slot_1");
        Assert.Equal(50, first.Choices.Length);
        Assert.Equal("Select a skill to see its modeled preview.", first.Status);

        first.Selected = Pick(first.Choices, "skill_assault_turret");
        await Resolved();
        Assert.Equal("skill_assault_turret", Build.ActiveSkillSelections["skill_slot_1"]);
        Assert.True(first.IsAccepted);
        Assert.False(first.HasStatus);
        Assert.True(first.HasDescription || first.Outputs.Length > 0);
        var damage = first.Outputs.Single(output => output.Name == "Estimated Damage");
        Assert.Contains(damage.Details, line => line.StartsWith("Formula: ", StringComparison.Ordinal));

        // Expertise is the skill's grade, 0 to 30.
        var expertise = first.Expertise!;
        Assert.Equal("Expertise", expertise.Name);
        Assert.Equal("0", expertise.ValueText);
        var before = double.Parse(Output(first, "Estimated Damage"), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        expertise.ValueText = "20";
        Assert.True(expertise.CommitValue());
        Assert.Equal(20, Build.SkillUpgrades["skill_slot_1"].Rank);
        await Resolved();
        Assert.True(double.Parse(Output(first, "Estimated Damage"), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture) > before);
        Assert.Contains(first.Outputs.Single(output => output.Name == "Estimated Damage").Details, line => line == "Uses installed grade: 20");

        expertise.ValueText = "31";
        Assert.False(expertise.CommitValue());
    }

    [Fact]
    public async Task ADetailNamesTheModsInstalledOnASharedInput()
    {
        // Explosive Sticky Bomb's Payload and Launcher can both roll Blast Radius.
        var first = Skill("skill_slot_1");
        first.Selected = Pick(first.Choices, "skill_explosive_sticky_bomb");
        await Resolved();
        var launcher = first.Mods.Single(mod => mod.Id == "launcher");
        launcher.Selected = launcher.Choices.Single(choice => choice.Id == "launcher_blast_radius");
        await Resolved();
        // The radius itself; from R108 the full-damage radius reads the same input.
        string Radius() => first.Outputs.Single(output => output.Name == "Estimated Radius Reference (m)")
            .Details.Single(line => line.Contains("Blast Radius", StringComparison.Ordinal) && line.StartsWith("Uses", StringComparison.Ordinal));
        Assert.StartsWith("Uses installed Launcher: Blast Radius: ", Radius(), StringComparison.Ordinal);
        var payload = first.Mods.Single(mod => mod.Id == "payload");
        payload.Selected = payload.Choices.Single(choice => choice.Id == "payload_blast_radius");
        await Resolved();
        Assert.StartsWith("Uses installed Payload: Blast Radius + Launcher: Blast Radius: ", Radius(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModsAreChosenByWhatTheyDoAndRolled()
    {
        var first = Skill("skill_slot_1");
        first.Selected = Pick(first.Choices, "skill_assault_turret");
        await Resolved();
        var firing = first.Mods.Single(mod => mod.Id == "firing_mechanism");
        Assert.True(firing.IsEnabled);
        Assert.Equal("Magnetic Rail · +2.5–5% Damage", Pick(firing.Choices, "magnetic_rail").Name);

        // A new mod starts at its highest roll, typed in percent.
        firing.Selected = Pick(firing.Choices, "magnetic_rail");
        Assert.Equal(0.05, Build.SkillMods["skill_slot_1"].Mods["firing_mechanism"].Value, 10);
        Assert.Equal("5", firing.ValueText);
        firing.ValueText = "3";
        Assert.True(firing.CommitValue());
        Assert.Equal(0.03, Build.SkillMods["skill_slot_1"].Mods["firing_mechanism"].Value, 10);
        await Resolved();
        firing.MaximumCommand.Execute(null);
        Assert.Equal(0.05, Build.SkillMods["skill_slot_1"].Mods["firing_mechanism"].Value, 10);

        // Another skill has other mods; the old ones go.
        first.Selected = Pick(first.Choices, "skill_striker_drone");
        Assert.DoesNotContain(first.Mods, mod => mod.Id == "firing_mechanism");
    }

    [Fact]
    public async Task ASecondSkillOfTheSameFamilyIsNotAccepted()
    {
        Skill("skill_slot_1").Selected = Pick(Skill("skill_slot_1").Choices, "skill_assault_turret");
        var second = Skill("skill_slot_2");
        second.Selected = Pick(second.Choices, "skill_sniper_turret");
        await Resolved();
        Assert.False(second.IsAccepted);
        Assert.Equal("Sniper Turret is not accepted in this loadout.", second.Status);
        Assert.Empty(second.Outputs);
        Assert.All(second.Mods, mod => Assert.False(mod.IsEnabled));

        // Clearing the slot returns it to empty, with no grade or mods.
        second.Selected = Choice.None;
        Assert.False(Build.ActiveSkillSelections.ContainsKey("skill_slot_2") && Build.ActiveSkillSelections["skill_slot_2"].Length > 0);
        Assert.Null(second.Expertise);
        Assert.Empty(second.Mods);
    }

    /// <summary>A skill slot's headline tile: the skill's headline rate, a shield's health, or why there is no figure (F25, F36).</summary>
    [Theory]
    [InlineData("skill_assault_turret", "Skill 1 DPS · Assault Turret", "Average DPS over Its Cooldown")]
    [InlineData("skill_bulwark_shield", "Skill 1 · Bulwark Shield", "Shield health")]
    [InlineData("skill_striker_drone", "Skill 1 DPS · Striker Drone", "Skill timing data pending")]
    [InlineData("skill_scanner_pulse", "Skill 1 DPS · Scanner Pulse", "This skill has no modeled damage")]
    public async Task EachSkillSlotHasAHeadlineTile(string skill, string label, string detail)
    {
        await Resolved();
        var tile = editor.Headline.OfType<SkillHeadlineViewModel>().Single(figure => figure.SlotId == "skill_slot_1");
        Assert.False(tile.IsVisible);

        Skill("skill_slot_1").Selected = Pick(Skill("skill_slot_1").Choices, skill);
        await Resolved();
        Assert.True(tile.IsVisible);
        Assert.Equal(label, tile.Label);
        Assert.StartsWith(detail, tile.Detail, StringComparison.Ordinal);
        if (detail is "Average DPS over Its Cooldown" or "Shield health")
        { Assert.True(double.Parse(tile.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture) > 0); }

        // The tiles sit where the layout's skill rates entry is, after the weapon's DPS.
        Assert.Equal(["Practical Reload-Cycle DPS", label], editor.Headline.Take(2).Select(figure => figure.Label));
    }
}
