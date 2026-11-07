using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>The loadout grid and the inspector: which tile is selected, and what each tile shows.</summary>
public sealed partial class EditorViewModel
{
    private object? inspected;
    private BuildSnapshot? previewBuild;
    private ResolvedBuild? preview;
    /// <summary>Resolves the picker's highlighted item off the UI thread, and the inspected slot's figures in its context.</summary>
    private LatestRevisionJob<PreviewResult?>? previewer;
    private LatestRevisionJob<ImmutableDictionary<string, double>>? slotFigures;
    /// <summary>The slot, item and revision previewed (or being), so a rebuilt row for the same item resolves nothing.</summary>
    private (string Slot, string Item, long Revision)? previewKey;
    private (SlotViewModel Slot, ItemPickerViewModel Picker)? previewing;

    /// <summary>A previewed build and its resolution.</summary>
    private sealed record PreviewResult(BuildSnapshot Build, ResolvedBuild? Resolved);

    /// <summary>The latest preview's resolution, for callers (and tests) that wait for it.</summary>
    public Task PreviewResolution { get; private set; } = Task.CompletedTask;
    /// <summary>The inspected slot's latest figures, for callers (and tests) that wait for them.</summary>
    public Task InspectedResolution { get; private set; } = Task.CompletedTask;
    private ImmutableDictionary<string, string> categoryColors = ImmutableDictionary<string, string>.Empty;

    /// <summary>The loadout's tile groups, as the Go app's: Agent (specialization, SHD Watch), the layout's slot groups, Skills.</summary>
    public ImmutableArray<TileGroupViewModel> TileGroups { get; private set; } = [];

    /// <summary>What the inspector edits: a slot, a skill slot, the specialization or the SHD Watch.</summary>
    public object? Inspected { get => inspected; private set => SetProperty(ref inspected, value); }

    public AccountUpgradesViewModel Account { get; private set; } = new([]);

    private IEnumerable<TileViewModel> Tiles => TileGroups.SelectMany(group => group.Tiles);

    private void BuildLoadout(PackLayout layout)
    {
        previewer = new(result => ui.Post(() => ShowPreview(result)));
        slotFigures = new(result => ui.Post(() => ShowSlotFigures(result)));
        categoryColors = layout.AttributeCategories.ToImmutableDictionary(category => category.ID, category => category.Color, StringComparer.Ordinal);
        Account = new(AccountUpgrades);
        var groups = new List<TileGroupViewModel>();
        var agent = new List<TileViewModel>();
        if (!Pack.Specializations.IsEmpty) { agent.Add(new("Specialization", Specialization, Select)); }
        if (!Pack.AccountUpgrades.IsEmpty) { agent.Add(new("Account upgrades", Account, Select)); }
        if (agent.Count > 0) { groups.Add(new("Agent", 0, [.. agent])); }
        var columns = layout.SlotGroups.ToDictionary(group => group.Name, group => (int)group.Columns, StringComparer.Ordinal);
        groups.AddRange(SlotGroups.Select(group => new TileGroupViewModel(group.Name, columns.GetValueOrDefault(group.Name),
            [.. group.Slots.Select(slot => new TileViewModel(slot.Name, slot, Select))])));
        if (!SkillSlots.IsEmpty) { groups.Add(new("Skills", 0, [.. SkillSlots.Select(skill => new TileViewModel(skill.Name, skill, Select))])); }
        TileGroups = [.. groups];
        // The inspector starts on the first slot, as Go's did.
        Select(Tiles.FirstOrDefault(tile => tile.Target is SlotViewModel) ?? Tiles.FirstOrDefault());
    }

    private void Select(TileViewModel? tile)
    {
        foreach (var each in Tiles) { each.IsSelected = each == tile; }
        Inspected = tile?.Target;
        ShowInspected();
    }

    /// <summary>
    /// The inspected slot's core stats and brand bonuses. Its core stats are worked out in the slot's context, as Go's: a
    /// weapon slot's as the weapon in hand, resolved again when another weapon is in hand.
    /// </summary>
    private void ShowInspected()
    {
        if (Inspected is not SlotViewModel slot) { return; }
        var build = session.Snapshot.State.Build;
        var item = Items.GetValueOrDefault(build.Equipment.GetValueOrDefault(slot.Id, ""));
        if (item is null || resolved is null) { (slot.CoreStats, slot.BrandBonuses) = ([], []); return; }
        slot.BrandBonuses = BrandBonusLines(resolved, item.ID);
        var snapshot = session.Snapshot;
        if (!Pack.Slots.Any(entry => entry.ID == slot.Id && entry.Weapon) || snapshot.State.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector, "") == slot.Id)
        {
            slotFigures!.Cancel();
            ShowCoreStats(slot, resolved.Calculation.Stats);
            return;
        }
        // Another weapon is in hand: this one's figures are resolved again, in the background, as the weapon in hand.
        var id = slot.Id;
        InspectedResolution = slotFigures!.Start(snapshot, (state, _) =>
        {
            return TryResolve(state.Pack, state.State with { Assumptions = state.State.Assumptions.SetItem(GamePack.WeaponSlotSelector, id) })?.Calculation.Stats
                ?? ImmutableDictionary<string, double>.Empty;
        });
    }

    private void ShowSlotFigures(JobResult<ImmutableDictionary<string, double>> result)
    {
        if (!slotFigures!.IsCurrent(result) || Inspected is not SlotViewModel slot) { return; }
        ShowCoreStats(slot, result.Value ?? ImmutableDictionary<string, double>.Empty);
    }

    private static void ShowCoreStats(SlotViewModel slot, ImmutableDictionary<string, double> stats) =>
        slot.CoreStats = [.. slot.CompareStats.Select(stat => new CompareValue(stat.ShortName.Length > 0 ? stat.ShortName : stat.Name,
            Formatting.Value(stat.Format, stats.GetValueOrDefault(stat.ID)), 0))];

    /// <summary>
    /// Previews the picker's highlighted item, as Go's picker did: the headline, summary and attribute counts show the
    /// build with it equipped (under the current assumptions), marked against the build, and the inspector shows the
    /// item with Equip. With no row, the preview ends and the inspector returns to the slot.
    /// </summary>
    internal void Preview(SlotViewModel slot, ItemRowViewModel? row, ItemPickerViewModel picker)
    {
        if (row is null)
        {
            EndPreview();
            Inspect(slot);
            return;
        }
        previewing = (slot, picker);
        var snapshot = session.Snapshot;
        var key = (slot.Id, row.Id, snapshot.Revision);
        if (key == previewKey)
        {
            // The same item on the same build (the picker rebuilt its rows): only its compared stats may be new.
            if (Inspected is CandidateViewModel candidate) { Inspected = candidate with { Marker = row.Marker, CoreStats = row.Stats }; }
            return;
        }
        previewKey = key;
        var id = row.Id;
        var slotId = slot.Id;
        // The edit's build, resolved in the background as the editor's own resolution is.
        PreviewResolution = previewer!.Start(snapshot, (state, _) =>
        {
            var outcome = session.Preview(state, [new EquipmentOperation(slotId, id)]);
            if (!outcome.Succeeded) { return null; }
            // Keep the candidate's Equip action available while either draft violates a limit.
            var comparable = LoadoutResolver.EquipmentLimitProblem(state.Pack.Pack, state.State.Build) is null;
            return new PreviewResult(outcome.State.Build, comparable ? TryResolve(state.Pack, outcome.State) : null);
        });
    }

    private void ShowPreview(JobResult<PreviewResult?> result)
    {
        if (!previewer!.IsCurrent(result) || previewing is not var (slot, picker) || Picker != picker) { return; }
        var row = picker.Highlighted;
        if (result.Value is not { } shown || row is null || Items.GetValueOrDefault(row.Id) is not { } item)
        {
            EndPreview();
            Inspect(slot);
            return;
        }
        (previewBuild, preview) = (shown.Build, shown.Resolved);
        var rarity = EntryOf(item).Rarity;
        Inspected = new CandidateViewModel(item.Name, rarity is null ? slot.Name : $"{slot.Name} · {rarity.Name}", rarity?.Color ?? "",
            Artwork.Item(item.ID, slot.Id), row.Marker, row.Stats, preview is null ? [] : BrandBonusLines(preview, item.ID), picker.EquipCommand,
            ItemCard(previewBuild, slot.Id, item));
        ShowFigures();
        ShowAttributeCounts(previewBuild);
    }

    /// <summary>Ends any preview: the figures, summary and counts show the build again.</summary>
    private void EndPreview()
    {
        previewer!.Cancel();
        (previewKey, previewing) = (null, null);
        if (preview is null && previewBuild is null) { return; }
        (previewBuild, preview) = (null, null);
        ShowFigures();
        ShowAttributeCounts(session.Snapshot.State.Build);
    }

    /// <summary>Selects the tile for a slot, skill slot, the specialization or the SHD Watch, as clicking it does.</summary>
    internal void Inspect(object target) => Select(Tiles.FirstOrDefault(tile => tile.Target == target));

    /// <summary>The skill tiles alone, once a resolution brings their first preview figure.</summary>
    private void ShowSkillTiles(BuildSnapshot build, ResolvedBuild resolved)
    {
        foreach (var tile in Tiles) { if (tile.Target is SkillSlotViewModel skill) { ShowSkill(tile, build, resolved, skill.Id); } }
    }

    /// <summary>Shows a state on the tiles; skill tiles show the last resolution's figure until the new one arrives.</summary>
    private void ShowTiles(BuildSnapshot build, ResolvedBuild? resolved)
    {
        foreach (var tile in Tiles)
        {
            switch (tile.Target)
            {
                case SlotViewModel slot: ShowSlot(tile, build, slot.Id); break;
                case SkillSlotViewModel skill: ShowSkill(tile, build, resolved, skill.Id); break;
                case SpecializationViewModel: ShowSpecialization(tile, build); break;
                case AccountUpgradesViewModel: ShowAccount(tile, build); break;
            }
        }
    }

    private void ShowSlot(TileViewModel tile, BuildSnapshot build, string slotId)
    {
        if (Items.GetValueOrDefault(build.Equipment.GetValueOrDefault(slotId, "")) is not { } item)
        {
            tile.Show("Empty", muted: true, art: Artwork.Item("", slotId));
            return;
        }
        var talentSlot = item.TalentSlots.FirstOrDefault(slot => (build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "").Length > 0);
        var talent = talentSlot is null ? null : Talents.GetValueOrDefault(build.TalentSelections[slotId][talentSlot.ID]);
        var rarity = EntryOf(item).Rarity;
        var subtitle = new[] { rarity?.Name ?? "", Configurable(item) ? ConfigSummary(build, slotId, item) : "" }.Where(text => text.Length > 0);
        var dots = AttributeDots(build, slotId, item);
        tile.Show(item.Name, string.Join(" · ", subtitle), accent: rarity?.Color ?? "", dots: dots, art: Artwork.Item(item.ID, slotId),
            talentName: talent?.Name ?? "", talentIcon: talent is null ? null : Artwork.Icon("talents", talent.ID),
            talentCommand: talentSlot is null ? null : new CommunityToolkit.Mvvm.Input.RelayCommand(() => CompareTalents(slotId, talentSlot.ID)));
    }

    private void ShowSkill(TileViewModel tile, BuildSnapshot build, ResolvedBuild? resolved, string slotId)
    {
        if (Skills.GetValueOrDefault(build.ActiveSkillSelections.GetValueOrDefault(slotId, "")) is not { } skill)
        {
            tile.Show("Empty", muted: true, art: new ItemArt(null, null, ArtGlyphs.Skill));
            return;
        }
        var first = resolved?.ActiveSkillPreviews.GetValueOrDefault(slotId) is { } preview && preview.SkillId == skill.ID ? preview.Outputs.FirstOrDefault() : null;
        tile.Show(skill.Name, first is null ? "" : $"{first.Name}: {Formatting.Value(first.Format, first.Value)}",
            art: new ItemArt(Artwork.Icon("skills", skill.ID), null, ArtGlyphs.Skill));
    }

    private void ShowSpecialization(TileViewModel tile, BuildSnapshot build)
    {
        if (Pack.Specializations.FirstOrDefault(entry => entry.ID == build.SpecializationId) is not { } specialization)
        {
            tile.Show("None", muted: true, art: new ItemArt(null, null, ArtGlyphs.Specialization));
            return;
        }
        var status = SpecializationRules.Status(specialization, build.SpecializationNodeSelections);
        var points = specialization.Budgets.Where(budget => budget.ParentID.Length == 0)
            .Select(budget => $"{status.Spent.GetValueOrDefault(budget.ID)} / {budget.Points} points");
        tile.Show(specialization.Name, string.Join(" · ", points), art: new ItemArt(Artwork.Icon("specializations", specialization.ID), null, ArtGlyphs.Specialization));
    }

    private void ShowAccount(TileViewModel tile, BuildSnapshot build)
    {
        var first = Pack.AccountUpgrades[0];
        var ranked = Pack.AccountUpgrades.Count(upgrade => build.AccountUpgrades.GetValueOrDefault(upgrade.ID) > 0);
        tile.Show($"{first.Name} {build.AccountUpgrades.GetValueOrDefault(first.ID)}", $"{ranked} of {Pack.AccountUpgrades.Length} ranked",
            art: new ItemArt(null, null, ArtGlyphs.Upgrades));
    }
}
