using System.Collections.Immutable;
using CharacterPlanner.Application.Assumptions;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The build editor (C4). Every edit is one operation applied to the session, so undo, redo and the history cover
/// the editor and any other client alike. Each new state is shown at once; its stats are resolved off the UI
/// thread, and only the latest revision's result is ever shown. The open library record and the version it was
/// read at are kept, so saving replaces exactly that record or reports that it changed meanwhile.
/// </summary>
/// <remarks>
/// All members are for the UI thread: background results arrive through <see cref="IUiDispatcher"/>. A superseded
/// resolution still runs to completion (Core's resolver takes no cancellation token); at under a millisecond per
/// build that's cheaper than the plumbing, and worth revisiting with upgrade ranking (F42).
/// </remarks>
public sealed partial class EditorViewModel : ViewModelBase, IDisposable
{
    private readonly PlannerSession session;
    private readonly IBuildLibrary library;
    private readonly IUiDispatcher ui;
    private readonly LatestRevisionJob<ResolvedBuild> resolver;
    private readonly ImmutableDictionary<string, StatRowViewModel> statRows;
    private readonly ImmutableArray<LayoutRarity> rarities;
    private long shownRevision = long.MinValue;
    private string shownName = "";
    private ResolvedBuild? resolved;
    private string? message;
    private StatRowViewModel? selectedStat;
    private ItemPickerViewModel? picker;
    private ImmutableArray<DiagnosticLine> diagnostics = [];

    public EditorViewModel(PlannerSession session, IBuildLibrary library, IUiDispatcher ui, PackArtwork? artwork = null, IAssumptionSetStore? assumptionSets = null)
    {
        Artwork = artwork ?? PackArtwork.None;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(ui);
        (this.session, this.library, this.ui) = (session, library, ui);
        Pack = session.Pack.Pack;
        Items = ById(Pack.Items, item => item.ID);
        Attributes = ById(Pack.Attributes, attribute => attribute.ID);
        Mods = ById(Pack.Mods, mod => mod.ID);
        Talents = ById(Pack.Talents, talent => talent.ID);
        Skills = ById(Pack.ActiveSkills, skill => skill.ID);
        // An item's brand or gear set: the first set that lists it.
        Sets = Pack.Sets.SelectMany(set => set.Items.Select(item => (Item: item, Set: set)))
            .GroupBy(entry => entry.Item, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First().Set, StringComparer.Ordinal);
        var stats = Statistics = ById(Pack.Stats, stat => stat.ID);

        var slots = Pack.Slots.ToImmutableDictionary(slot => slot.ID, StringComparer.Ordinal);
        // A pack without a layout still edits: one group holds every slot, and there are no headline or summary stats.
        var layout = Pack.Presentation.Layout ?? new PackLayout { SlotGroups = [new() { Name = "Equipment", Slots = [.. Pack.Slots.Select(slot => slot.ID)] }] };
        rarities = layout.Rarities;
        Rarities = [.. layout.Rarities.Select(entry => new Choice(entry.ID, entry.Name))];
        // Assumptions by the layout's sections; one without a section goes in the layout's default section.
        var sections = layout.AssumptionSections.Select(section => section.Name).ToList();
        var fallback = layout.AssumptionSections.FirstOrDefault(section => section.Default)?.Name ?? sections.FirstOrDefault() ?? "Assumptions";
        if (!sections.Contains(fallback)) { sections.Add(fallback); }
        var assumptions = AssumptionViewModel.Cards(this, Pack)
            .Select(entry => (Section: sections.Contains(entry.Section) ? entry.Section : fallback, entry.Assumption))
            .ToList();
        AssumptionSections = [.. sections.Select(section => new AssumptionSectionViewModel(section,
            [.. assumptions.Where(entry => entry.Section == section).Select(entry => entry.Assumption).OrderBy(assumption => assumption.Name, StringComparer.CurrentCultureIgnoreCase)]))
            .Where(section => !section.Assumptions.IsEmpty)];
        AssumptionsTab = new AssumptionsTabViewModel(AssumptionSections, new AssumptionSetBarViewModel(this, assumptionSets));
        AccountUpgrades = [.. Pack.AccountUpgrades.Select(definition => new RankViewModel(this, definition.ID, definition.Name, 0, definition.MaxRank, rank => new AccountUpgradeOperation(definition.ID, rank)))];
        // A picker row compares at most three of its group's stats, as the Go app's did.
        SlotGroups = [.. layout.SlotGroups.Select(group => new SlotGroupViewModel(group.Name,
            [.. group.Slots.Where(slots.ContainsKey).Select(id => new SlotViewModel(this, slots[id], [.. group.CompareStats.Where(stats.ContainsKey).Take(3).Select(stat => stats[stat])]))]))];
        SkillSlots = [.. Pack.ActiveSkillSlots.Select(slot => new SkillSlotViewModel(this, slot))];
        Specialization = new SpecializationViewModel(this);
        Party = new PartyViewModel(this);
        Notes = new NotesViewModel(this, ui);
        Sharing = new SharingViewModel(this);
        // What an empty build resolves to: conditional figures and rows hide while they equal it, as in the Go app.
        Baseline = EmptyBuildStats(session.Pack, Pack);
        // The skill rates entry is a tile for each skill slot.
        Headline = [.. layout.Headline.SelectMany(entry => entry.SkillRates
            ? Pack.ActiveSkillSlots.Select(slot => (FigureViewModel)new SkillHeadlineViewModel(this, slot))
            : [Figure(entry, stats)])];
        SelectStatCommand = new RelayCommand<StatRowViewModel>(row => SelectedStat = row);
        var rows = new Dictionary<string, StatRowViewModel>(StringComparer.Ordinal);
        var labels = StatRowViewModel.Labels(layout.Summary, stats);
        Summary = [.. layout.Summary.Select(group => new StatGroupViewModel(group.Name, [.. group.Stats.Concat(group.Conditional)
            .Where(stats.ContainsKey)
            .Select(id => rows.TryGetValue(id, out var row) ? row : rows[id] = new StatRowViewModel(stats[id],
                group.Conditional.Contains(id) && !group.Stats.Contains(id), labels.GetValueOrDefault(id, ""), Baseline.GetValueOrDefault(id), SelectStatCommand.Execute))]))];
        statRows = rows.ToImmutableDictionary(StringComparer.Ordinal);
        Stats = new StatsPageViewModel(layout.Summary, Pack.Stats, Baseline, SelectStatCommand.Execute);
        // The summary's weapon tabs ("Primary", "Secondary", "Sidearm") and attribute counts, as Go's.
        WeaponTabs = [.. Pack.Slots.Where(slot => slot.Weapon).Select(slot => new WeaponTabViewModel(slot.ID,
            slot.Name.EndsWith(" Weapon", StringComparison.Ordinal) && slot.Name.Length > " Weapon".Length ? slot.Name[..^" Weapon".Length] : slot.Name, ToggleWeapon))];
        AttributeCounts = [.. layout.AttributeCategories.Select(category => new AttributeCountViewModel(category.Name, category.Color))];
        countedSlots = [.. layout.SlotGroups.Where(group => group.CountAttributes).SelectMany(group => group.Slots)];

        UndoCommand = new RelayCommand(Undo, () => CanUndo);
        RedoCommand = new RelayCommand(Redo, () => CanRedo);
        RenameCommand = new RelayCommand(Rename);
        // Library actions share one busy flag, so a save can't race an open or another save over which record is open.
        NewBuildCommand = new AsyncRelayCommand(NewBuildAsync, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(asNew: false), () => !IsBusy);
        SaveAsNewCommand = new AsyncRelayCommand(() => SaveAsync(asNew: true), () => !IsBusy);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand<LibraryEntry>(OpenAsync, _ => !IsBusy);
        AllStatsCommand = new RelayCommand(() => Host?.ShowAllStats());

        BuildLoadout(layout);
        resolver = new(result => ui.Post(() => ShowResolution(result)));
        session.Changed += OnSessionChanged;
        baseline = Document(session.Snapshot.State.Build);
        Show(session.Snapshot, force: true);
    }

    private static ImmutableDictionary<string, T> ById<T>(ImmutableArray<T> values, Func<T, string> id) =>
        values.GroupBy(id, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    internal GamePack Pack { get; }
    /// <summary>The pack's artwork; none in tests that don't need it.</summary>
    internal PackArtwork Artwork { get; private set; }
    internal PlannerSession Session => session;
    internal ImmutableDictionary<string, ItemDefinition> Items { get; }
    internal ImmutableDictionary<string, AttributeDefinition> Attributes { get; }
    internal ImmutableDictionary<string, ModDefinition> Mods { get; }
    internal ImmutableDictionary<string, TalentDefinition> Talents { get; }
    internal ImmutableDictionary<string, ActiveSkillDefinition> Skills { get; }
    internal ImmutableDictionary<string, StatDefinition> Statistics { get; }
    /// <summary>The stats of an empty build with no assumptions, which conditional figures and rows are compared with.</summary>
    internal ImmutableDictionary<string, double> Baseline { get; }
    /// <summary>Each item's brand or gear set, by item id; an item in none is absent.</summary>
    internal ImmutableDictionary<string, SetDefinition> Sets { get; }
    /// <summary>True while the editor shows a state, so pickers updating to match don't apply edits back.</summary>
    internal bool Refreshing { get; private set; }

    public ImmutableArray<SlotGroupViewModel> SlotGroups { get; }
    /// <summary>The active-skill slots, each with its skill, grade, mods and preview.</summary>
    public ImmutableArray<SkillSlotViewModel> SkillSlots { get; }
    /// <summary>The specialization and its tree.</summary>
    public SpecializationViewModel Specialization { get; }
    /// <summary>The pack's rarities, in the order an item's rarity is decided.</summary>
    public ImmutableArray<Choice> Rarities { get; }
    /// <summary>The SHD Watch (account) upgrades, each a rank.</summary>
    public ImmutableArray<RankViewModel> AccountUpgrades { get; }
    /// <summary>The assumptions, by section; each shows only while the build reads it or it is set.</summary>
    public ImmutableArray<AssumptionSectionViewModel> AssumptionSections { get; }
    /// <summary>A tab per weapon slot; the active one is the weapon in hand.</summary>
    public ImmutableArray<WeaponTabViewModel> WeaponTabs { get; }
    public bool HasWeaponTabs => !WeaponTabs.IsEmpty;
    /// <summary>Each attribute category's configured core and other attributes on the counted gear.</summary>
    public ImmutableArray<AttributeCountViewModel> AttributeCounts { get; }
    public bool HasAttributeCounts => !AttributeCounts.IsEmpty;
    private readonly ImmutableArray<string> countedSlots;

    /// <summary>Puts a weapon in hand, or with its own tab, takes it out of hand.</summary>
    private void ToggleWeapon(string slotId) =>
        Apply(session.Snapshot.State.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector, "") == slotId
            ? ContextOperation.Cleared(GamePack.WeaponSlotSelector)
            : new ContextOperation(GamePack.WeaponSlotSelector, Text: slotId));

    /// <summary>Counts the configured attributes of each category on the counted gear (Go's attributeCounts).</summary>
    private void ShowAttributeCounts(BuildSnapshot build)
    {
        var rolls = countedSlots.Where(slot => build.Equipment.GetValueOrDefault(slot, "").Length > 0)
            .SelectMany(slot => build.AttributeRolls.GetValueOrDefault(slot)?.Values ?? Enumerable.Empty<AttributeRoll>())
            .Select(roll => Attributes.GetValueOrDefault(roll.AttributeId)).OfType<AttributeDefinition>().ToList();
        foreach (var (count, category) in AttributeCounts.Zip(Pack.Presentation.Layout?.AttributeCategories ?? []))
        {
            count.Core = rolls.Count(attribute => attribute.Category == category.ID && attribute.Core);
            count.Other = rolls.Count(attribute => attribute.Category == category.ID && !attribute.Core);
        }
    }
    public ImmutableArray<FigureViewModel> Headline { get; }
    public ImmutableArray<StatGroupViewModel> Summary { get; }
    /// <summary>The Party tab: teammates and their buffs (F44).</summary>
    public PartyViewModel Party { get; }
    /// <summary>The Notes tab (F47).</summary>
    public NotesViewModel Notes { get; }
    /// <summary>Share and Import (F51, F93).</summary>
    public SharingViewModel Sharing { get; }

    /// <summary>
    /// Reads a share code for Import and for a party member's default name. The app reads Go-era codes too (through
    /// Legacy, wired by <see cref="MainViewModel"/>); without one, only this app's codes are read.
    /// </summary>
    public Func<string, ImportedBuild> ReadShareCode { get; init; } = ImportedBuild.ReadAppCode;

    /// <summary>The latest background resolution, for callers (and tests) that need to wait for the stats.</summary>
    public Task Resolution { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The window the editor is shown in: it asks before unsaved changes are discarded and shows the editor's dialogs.
    /// Without one, unsaved changes are never discarded and no dialog opens.
    /// </summary>
    public IEditorHost? Host { get; set; }

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand NewBuildCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveAsNewCommand { get; }
    public IAsyncRelayCommand RefreshLibraryCommand { get; }
    public IAsyncRelayCommand<LibraryEntry> OpenCommand { get; }
    /// <summary>Shows a stat's details; for rows in lists that don't select.</summary>
    public IRelayCommand<StatRowViewModel> SelectStatCommand { get; }
    /// <summary>The summary's All stats: asks the window to show the Stats tab.</summary>
    public IRelayCommand AllStatsCommand { get; }
    /// <summary>The Stats tab: every derived stat (F37).</summary>
    public StatsPageViewModel Stats { get; }
    /// <summary>The Assumptions tab's search, note and assumption set bar (F45, F46).</summary>
    public AssumptionsTabViewModel AssumptionsTab { get; }

    /// <summary>The build's name as typed; <see cref="RenameCommand"/> applies it.</summary>
    public string BuildName { get; set => SetProperty(ref field, value ?? ""); } = "";

    /// <summary>Why the last request was refused, or null.</summary>
    public string? Message { get => message; set => SetProperty(ref message, value); }

    public bool CanUndo { get; private set { if (SetProperty(ref field, value)) { UndoCommand.NotifyCanExecuteChanged(); } } }
    public bool CanRedo { get; private set { if (SetProperty(ref field, value)) { RedoCommand.NotifyCanExecuteChanged(); } } }

    /// <summary>The open item picker, which takes the equipment column's place, or null.</summary>
    public ItemPickerViewModel? Picker { get => picker; private set => SetProperty(ref picker, value); }

    /// <summary>Opens the item picker for a slot, or with null closes it.</summary>
    internal void Browse(SlotViewModel? slot)
    {
        var closing = Picker;
        Picker?.Dispose();
        // Closing a picker, or opening another, ends its preview.
        EndPreview();
        if (slot is null)
        {
            Picker = null;
            // Back returns the inspector to the slot, as Go's did.
            if (closing is not null && SlotGroups.SelectMany(group => group.Slots).FirstOrDefault(entry => entry.Id == closing.SlotId) is { } browsed)
            { Inspect(browsed); }
            return;
        }
        // It sorts by the slot's compared stats, then the summary's.
        var sortStats = slot.CompareStats.Concat(Summary.SelectMany(group => group.Rows).Select(row => Statistics.GetValueOrDefault(row.Id)).OfType<StatDefinition>())
            .DistinctBy(stat => stat.ID, StringComparer.Ordinal).ToImmutableArray();
        // The picker's slot is the one inspected while it is open (its candidate shows there).
        Inspect(slot);
        Picker = new ItemPickerViewModel(this, slot, sortStats, ui);
        Picker.Show(session.Snapshot);
    }
    /// <summary>The diagnostics strip: each error, then warnings collapsed by kind.</summary>
    public ImmutableArray<DiagnosticLine> Diagnostics { get => diagnostics; private set { if (SetProperty(ref diagnostics, value)) { OnPropertyChanged(nameof(HasDiagnostics)); } } }
    public bool HasDiagnostics => !Diagnostics.IsEmpty;

    /// <summary>The weapon the headline's weapon figures use: "Primary Weapon · ACR", "… · empty", or none selected.</summary>
    public string ActiveWeaponText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>The pack's name, for the top bar.</summary>
    public string PackName => Pack.Name;

    /// <summary>What the user is running, for About (G340): the program build, the game release and the data version.</summary>
    public ImmutableArray<(string Caption, string Value)> About =>
    [
        ("Program build", typeof(EditorViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "Development build"),
        ("Game version", Pack.GameVersion.Length > 0 ? $"{Pack.Name} · {Pack.GameVersion}" : Pack.Name),
        ("Game data", Pack.Version)
    ];

    /// <summary>
    /// Applies operations as one edit and returns whether it was accepted. A refusal is shown in <see cref="Message"/>,
    /// and the pickers return to the state.
    /// </summary>
    internal bool Apply(params BuildOperation[] operations) => Apply(null, operations);

    /// <summary>Applies an edit that merges with the previous one of the same key, as one slider drag is one undo step.</summary>
    internal bool ApplyCoalescing(string key, params BuildOperation[] operations) => Apply(key, operations);

    private bool Apply(string? coalescingKey, BuildOperation[] operations)
    {
        var result = session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop, coalescingKey);
        Message = result.Succeeded ? null : result.OperationErrors.FirstOrDefault()?.Message ?? result.Error?.Message;
        Show(result.Snapshot, force: !result.Succeeded);
        return result.Succeeded;
    }

    private void Undo() => Report(session.Undo(session.Snapshot.Revision));
    private void Redo() => Report(session.Redo(session.Snapshot.Revision));

    private void Rename()
    {
        if (BuildName.Trim().Length == 0)
        {
            Message = "The build name must not be blank";
            BuildName = shownName;
            return;
        }
        Apply(new NameOperation(BuildName.Trim()));
    }

    private void Report(SessionResult result)
    {
        Message = result.Succeeded ? null : result.Error?.Message;
        Show(result.Snapshot, force: !result.Succeeded);
    }

    private void OnSessionChanged(SessionSnapshot snapshot) => ui.Post(() => Show(session.Snapshot, force: false));

    /// <summary>Shows a state in the pickers (without applying anything back) and starts resolving its stats.</summary>
    private void Show(SessionSnapshot snapshot, bool force)
    {
        if (!force && snapshot.Revision == shownRevision) { return; }
        var fresh = snapshot.Revision != shownRevision;
        shownRevision = snapshot.Revision;
        var build = snapshot.State.Build;
        Refreshing = true;
        try
        {
            foreach (var slot in SlotGroups.SelectMany(group => group.Slots)) { slot.Refresh(build); }
            foreach (var skill in SkillSlots) { skill.Refresh(build); }
            Specialization.Refresh(build);
            ShowTiles(build, resolved);
            Party.Refresh(build);
            Notes.Refresh(build.Meta.Notes);
            foreach (var account in Pack.AccountUpgrades.Zip(AccountUpgrades)) { account.Second.Refresh(build.AccountUpgrades.GetValueOrDefault(account.First.ID)); }
            foreach (var section in AssumptionSections)
            {
                foreach (var assumption in section.Assumptions) { assumption.Refresh(snapshot.State.Assumptions); }
                section.Update();
            }
            AssumptionsTab.Sets.Refresh(snapshot.State.Assumptions);
            var weapon = snapshot.State.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
            foreach (var tab in WeaponTabs) { tab.IsActive = tab.SlotId == weapon; }
            ShowAttributeCounts(previewBuild ?? build);
            var slotName = Pack.Slots.FirstOrDefault(slot => slot.ID == weapon)?.Name ?? weapon;
            var held = Items.GetValueOrDefault(build.Equipment.GetValueOrDefault(weapon, ""));
            ActiveWeaponText = weapon.Length == 0 ? "No active weapon selected" : held is null ? $"{slotName} · empty" : $"{slotName} · {held.Name}";
            // A name being typed is kept unless the stored name itself changed.
            if (BuildName == shownName || build.Meta.Name != shownName) { BuildName = build.Meta.Name; }
            shownName = build.Meta.Name;
        }
        finally { Refreshing = false; }
        (CanUndo, CanRedo) = (snapshot.CanUndo, snapshot.CanRedo);
        UpdateDirty(snapshot);
        rankUpgradesCommand?.NotifyCanExecuteChanged();
        if (fresh)
        {
            Resolution = resolver.Start(snapshot, static (state, _) => BuildResolver.Resolve(state.Pack, state.State.Build, state.State.Assumptions));
            Picker?.Show(snapshot);
        }
    }

    public void Dispose()
    {
        ArtworkManager?.Dispose();
        session.Changed -= OnSessionChanged;
        resolver.Dispose();
        simulationOwnerDisposed = true;
        simulate?.Dispose();
        Picker?.Dispose();
        previewer?.Dispose();
        slotFigures?.Dispose();
        RankUpgrades?.Dispose();
    }
}
