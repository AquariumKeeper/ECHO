using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Simulation;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>Runs captured session simulations and applies only current notifications on the UI dispatcher.</summary>
public sealed class SimulateViewModel : ViewModelBase, IDisposable
{
    public const string Intro = "Plays the build against a stream of the selected enemy target. Unsupported mechanics are reported before a result is shown.";
    public const string NoSwaps = "No swaps";
    public const string SwapsUnderFire = "Weapon swaps under incoming fire require talent triggers";
    public const string SwapWhenEmpty = "Swap instead of reloading";
    public const string SwapAfterKill = "Swap after each kill";
    public const string IncomingLabel = "Enemies fire back (armor breaks, going down ends your damage)";
    private readonly PlannerSession session;
    private readonly IUiDispatcher ui;
    private readonly SessionSimulation simulation;
    private bool disposed;
    private long requestRevision;
    private ImmutableDictionary<string, string> seenEquipment = ImmutableDictionary<string, string>.Empty;
    private string seenActive = "";
    private string seenAttackers = "";
    private int observesSessionChanges;
    private int refreshPosted;
    private string? message;
    private bool enemiesShrink;
    private string fightLength = "60", fights = "500", swaps = NoSwaps, swapTime = "1", distance = "", seed = "1", pickupDelay = "2", pickupChance = "100", clearKills = "", peekSeconds = "", coverSeconds = "", peekHitPercent = "", enemiesAtOnce = "", armorKits = "", armorKitAtPercent = "", focusSeconds = "", enemySpacing = "";
    private bool averageFight, incoming, includeSkills, includeShield, captureEvents;
    private bool driveBuffs = true;
    private Choice swapWeapon = Choice.None;
    private (Core.Builds.BuildSnapshot, ImmutableDictionary<string, string>, bool, bool, string)? offeredFor;

    public SimulateViewModel(PlannerSession session, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);
        this.session = session;
        this.ui = ui;
        RunCommand = new RelayCommand(Run, () => !disposed && !IsRunning && Problem is null);
        // ADR 0014: a "Use when" for each skill slot the pack has.
        SkillUses = [.. session.Pack.Pack.ActiveSkillSlots.Select(slot => new SkillUseViewModel(slot.ID, slot.Name, Check))];
        OfferSkillUseKeys();
        CancelCommand = new RelayCommand(() => Invalidate("Simulation cancelled."), () => IsRunning);
        // Mixed targets (P10): the pack's named enemy target profiles, without "no target". The engine takes whole profile
        // numbers; a pack whose input has no named options offers none.
        var targetOptions = session.Pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == DummyFightOptions.TargetProfileInput)?.Options ?? [];
        TargetProfiles = targetOptions.IsDefaultOrEmpty ? [] : [.. targetOptions
            .Where(option => option.Value >= 1 && option.Value <= int.MaxValue && option.Value == Math.Floor(option.Value))
            .Select(option => new Choice(((int)option.Value).ToString(CultureInfo.InvariantCulture), option.Label))];
        sequenceProfile = TargetProfiles.FirstOrDefault() ?? Choice.None;
        AddTargetCommand = new RelayCommand(() => TargetSequence = TargetSequence.Add(SequenceProfile),
            () => !disposed && SequenceProfile.Id.Length > 0 && TargetSequence.Length < DummyFightOptions.MaximumTargetSequence);
        RemoveLastTargetCommand = new RelayCommand(() => TargetSequence = TargetSequence.RemoveAt(TargetSequence.Length - 1), () => !disposed && !TargetSequence.IsEmpty);
        ClearTargetsCommand = new RelayCommand(() => TargetSequence = [], () => !disposed && !TargetSequence.IsEmpty);
        // Target distances (P10): typed one at a time, so a decimal comma never splits a list.
        AddDistanceCommand = new RelayCommand(() =>
        {
            if (EntryDistance() is { } meters && TargetDistances.Length < DummyFightOptions.MaximumTargetSequence) { TargetDistances = TargetDistances.Add(meters); DistanceEntry = ""; }
        },
            () => !disposed && EntryDistance() is not null && TargetDistances.Length < DummyFightOptions.MaximumTargetSequence);
        RemoveLastDistanceCommand = new RelayCommand(() => TargetDistances = TargetDistances.RemoveAt(TargetDistances.Length - 1), () => !disposed && !TargetDistances.IsEmpty);
        ClearDistancesCommand = new RelayCommand(() => TargetDistances = [], () => !disposed && !TargetDistances.IsEmpty);
        RefreshWeapons(session.Snapshot);
        seenAttackers = Attackers(session.Snapshot);
        simulation = new(session, result => ui.Post(() => Show(result)), update => ui.Post(() => ShowProgress(update)));
        Problem = LoadoutResolver.EquipmentLimitProblem(session.Pack.Pack, session.Snapshot.State.Build)?.Message;
        session.Changed += OnChanged;
    }

    public ImmutableArray<string> SwapOptions { get; } = [NoSwaps, SwapWhenEmpty, SwapAfterKill];

    private Choice sequenceProfile;
    /// <summary>The pack's enemy target profiles that a mixed-target sequence may hold.</summary>
    public ImmutableArray<Choice> TargetProfiles { get; }
    public bool HasTargetProfiles => !TargetProfiles.IsEmpty;
    /// <summary>The profile the Add button appends.</summary>
    public Choice SequenceProfile
    {
        get => sequenceProfile;
        set { if (value is not null && SetProperty(ref sequenceProfile, value)) { AddTargetCommand.NotifyCanExecuteChanged(); } }
    }
    /// <summary>Targets cycle through these profiles in order; empty for the chosen enemy target throughout.</summary>
    public ImmutableArray<Choice> TargetSequence
    {
        get => field;
        private set
        {
            if (!SetProperty(ref field, value)) { return; }
            OnPropertyChanged(nameof(TargetSequenceText));
            AddTargetCommand.NotifyCanExecuteChanged();
            RemoveLastTargetCommand.NotifyCanExecuteChanged();
            ClearTargetsCommand.NotifyCanExecuteChanged();
            Check();
        }
    } = [];
    public string TargetSequenceText => TargetSequence.IsEmpty
        ? "No mixed targets: every target is the chosen enemy target."
        : TargetSequence.Length == 1 ? $"1 target, repeating: {TargetSequence[0].Name}"
        : $"{TargetSequence.Length} targets, then repeating: {string.Join("; ", TargetSequence.Select(choice => choice.Name))}";
    public IRelayCommand AddTargetCommand { get; }
    public IRelayCommand RemoveLastTargetCommand { get; }
    public IRelayCommand ClearTargetsCommand { get; }

    /// <summary>The distance the Add button appends, in metres, as typed.</summary>
    public string DistanceEntry
    {
        get => field;
        set { if (SetProperty(ref field, value ?? "")) { AddDistanceCommand.NotifyCanExecuteChanged(); Check(); } }
    } = "";
    /// <summary>Targets cycle through these distances in order; empty for the target distance above throughout.</summary>
    public ImmutableArray<double> TargetDistances
    {
        get => field;
        private set
        {
            if (!SetProperty(ref field, value)) { return; }
            OnPropertyChanged(nameof(TargetDistancesText));
            AddDistanceCommand.NotifyCanExecuteChanged();
            RemoveLastDistanceCommand.NotifyCanExecuteChanged();
            ClearDistancesCommand.NotifyCanExecuteChanged();
            Check();
        }
    } = [];
    public string TargetDistancesText => TargetDistances.IsEmpty
        ? "No target distances: every target is at the target distance above."
        : TargetDistances.Length == 1 ? $"1 distance, repeating: {Meters(TargetDistances[0])}"
        : $"{TargetDistances.Length} distances, then repeating: {string.Join("; ", TargetDistances.Select(Meters))}";
    public IRelayCommand AddDistanceCommand { get; }
    public IRelayCommand RemoveLastDistanceCommand { get; }
    public IRelayCommand ClearDistancesCommand { get; }
    private static string Meters(double meters) => meters.ToString("G", CultureInfo.CurrentCulture) + " m";
    // The typed distance when it is one the form takes, or null.
    private double? EntryDistance() => ParseDistance(DistanceEntry, CultureInfo.CurrentCulture);
    // A distance the form takes, for the target distance and each target's: above 0 and up to 100 m.
    private static double? ParseDistance(string text, IFormatProvider culture) =>
        double.TryParse(text.Trim(), NumberStyles.Float, culture, out var meters) && double.IsFinite(meters) && meters > 0 && meters <= 100 ? meters : null;
    public ImmutableArray<Choice> SwapWeapons { get; private set => SetProperty(ref field, value); } = [];
    public Choice SwapWeapon { get => swapWeapon; set { if (value is not null && SetProperty(ref swapWeapon, value)) { Check(); } } }
    public string FightLength { get => fightLength; set { if (SetProperty(ref fightLength, value ?? "")) { Check(); } } }
    public string Fights { get => fights; set { if (SetProperty(ref fights, value ?? "")) { Check(); } } }
    public string Swaps { get => swaps; set { if (value is not null && SetProperty(ref swaps, value)) { Check(); } } }
    public string SwapTime { get => swapTime; set { if (SetProperty(ref swapTime, value ?? "")) { Check(); } } }
    public string Distance { get => distance; set { if (SetProperty(ref distance, value ?? "")) { Check(); } } }
    public string Seed { get => seed; set { if (SetProperty(ref seed, value ?? "")) { Check(); } } }
    /// <summary>Seconds from a kill until its trophy is collected (Kill Confirmed); an assumption about play.</summary>
    public string PickupDelay { get => pickupDelay; set { if (SetProperty(ref pickupDelay, value ?? "")) { Check(); } } }
    /// <summary>The percentage of trophies collected at all.</summary>
    public string PickupChance { get => pickupChance; set { if (SetProperty(ref pickupChance, value ?? "")) { Check(); } } }
    /// <summary>How many targets each fight's time to clear counts; empty for none.</summary>
    public string ClearKills { get => clearKills; set { if (SetProperty(ref clearKills, value ?? "")) { Check(); } } }
    /// <summary>Seconds peeking from cover in each cycle (ADR 0009); empty for a fight without cover.</summary>
    public string PeekSeconds { get => peekSeconds; set { if (SetProperty(ref peekSeconds, value ?? "")) { Check(); } } }
    /// <summary>Seconds fully behind cover in each cycle; empty for a fight without cover.</summary>
    public string CoverSeconds { get => coverSeconds; set { if (SetProperty(ref coverSeconds, value ?? "")) { Check(); } } }
    /// <summary>The percentage of the normal enemy hit chance that lands while peeking, needed with incoming fire. There is no default.</summary>
    public string PeekHitPercent { get => peekHitPercent; set { if (SetProperty(ref peekHitPercent, value ?? "")) { Check(); } } }
    /// <summary>How many enemies fire at once (ADR 0008); empty for one at a time.</summary>
    public string EnemiesAtOnce { get => enemiesAtOnce; set { if (SetProperty(ref enemiesAtOnce, value ?? "")) { Check(); } } }
    /// <summary>The meters between neighbouring enemies on a line, for talents reaching other enemies (ADR 0013); empty for none.</summary>
    public string EnemySpacing { get => enemySpacing; set { if (SetProperty(ref enemySpacing, value ?? "")) { Check(); } } }
    /// <summary>Nobody replaces the last enemies, so fire falls with each kill; Targets to clear says how many there are.</summary>
    public bool EnemiesShrink { get => enemiesShrink; set { if (SetProperty(ref enemiesShrink, value)) { Check(); } } }
    /// <summary>Seconds on each enemy before moving to the next (ADR 0011); empty to shoot each until it dies. There is no default.</summary>
    public string FocusSeconds { get => focusSeconds; set { if (SetProperty(ref focusSeconds, value ?? "")) { Check(); } } }
    /// <summary>The armor kits carried (ADR 0010); empty for none.</summary>
    public string ArmorKits { get => armorKits; set { if (SetProperty(ref armorKits, value ?? "")) { Check(); } } }
    /// <summary>The share of max armor below which a kit is used, needed with kits. There is no default.</summary>
    public string ArmorKitAtPercent { get => armorKitAtPercent; set { if (SetProperty(ref armorKitAtPercent, value ?? "")) { Check(); } } }
    public bool Incoming { get => incoming; set { if (SetProperty(ref incoming, value)) { Check(); } } }
    public bool IncludeSkills { get => includeSkills; set { if (SetProperty(ref includeSkills, value)) { Check(); } } }
    /// <summary>Each skill slot's "Use when" (ADR 0014), shown with damaging skills included.</summary>
    public ImmutableArray<SkillUseViewModel> SkillUses { get; }
    public bool IncludeShield { get => includeShield; set { if (SetProperty(ref includeShield, value)) { Check(); } } }
    public bool DriveBuffs { get => driveBuffs; set { if (SetProperty(ref driveBuffs, value)) { Check(); } } }
    public bool CaptureEvents { get => captureEvents; set { if (SetProperty(ref captureEvents, value)) { Check(); } } }

    public bool AverageFight { get => averageFight; set { if (SetProperty(ref averageFight, value)) { OnPropertyChanged(nameof(RandomFights)); Check(); } } }
    public bool RandomFights { get => !averageFight; set => AverageFight = !value; }
    public string? Problem
    {
        get => field;
        private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasProblem)); OnPropertyChanged(nameof(Status)); RunCommand.NotifyCanExecuteChanged(); } }
    }
    public string? RunError
    {
        get => field;
        private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasProblem)); OnPropertyChanged(nameof(Status)); } }
    }
    public bool HasProblem => Problem is not null || RunError is not null;
    public string Status => Problem ?? RunError ?? message ?? Intro;
    public bool IsRunning
    {
        get => field;
        private set { if (SetProperty(ref field, value)) { RunCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); } }
    }
    public double Progress { get; private set => SetProperty(ref field, value); }
    public DummyFightResult? Result
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(TimelineUnavailable));
            }
        }
    }
    public SimulationDistributions Distributions { get; private set => SetProperty(ref field, value); } = SimulationDistributions.Empty;
    public SimulationIncomingReport IncomingReport { get; private set => SetProperty(ref field, value); } = SimulationIncomingReport.Empty;
    public SimulationUptimeReport UptimeReport { get; private set => SetProperty(ref field, value); } = SimulationUptimeReport.Empty;
    public SimulationEventReport EventReport { get; private set => SetProperty(ref field, value); } = SimulationEventReport.Empty;
    public string ResultText { get; private set => SetProperty(ref field, value); } = "";
    public string Limitations { get; private set => SetProperty(ref field, value); } = "";
    private SimulationTimeline timeline = SimulationTimeline.Empty;
    public SimulationTimeline Timeline
    {
        get => timeline;
        private set
        {
            if (ReferenceEquals(timeline, value)) { return; }
            timeline = value;
            // Restore the selection invariant before publishing the new series to bindings.
            SelectedTimelineIndex = value.Bins.IsEmpty ? -1 : 0;
            OnPropertyChanged(nameof(Timeline));
            OnPropertyChanged(nameof(HasTimeline));
            OnPropertyChanged(nameof(TimelineUnavailable));
            OnPropertyChanged(nameof(TimelineDetails));
        }
    }
    public bool HasTimeline => !Timeline.Bins.IsEmpty;
    public bool TimelineUnavailable => Result is not null && !HasTimeline;
    public int SelectedTimelineIndex
    {
        get => field;
        set
        {
            var next = Timeline.Bins.IsEmpty ? -1 : Math.Clamp(value, 0, Timeline.Bins.Length - 1);
            if (SetProperty(ref field, next)) { OnPropertyChanged(nameof(TimelineDetails)); }
            if (value != next) { OnPropertyChanged(nameof(SelectedTimelineIndex)); }
        }
    } = -1;
    public string TimelineDetails => SelectedTimelineIndex >= 0 ? Timeline.Bins[SelectedTimelineIndex].Details : "";
    public Task Running { get; private set; } = Task.CompletedTask;
    public IRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }

    private void Invalidate(string? status)
    {
        simulation.Cancel();
        Volatile.Write(ref observesSessionChanges, 0);
        RunError = null;
        IsRunning = false;
        SetResult(null);
        Timeline = SimulationTimeline.Empty;
        ResultText = Limitations = "";
        Progress = 0;
        message = status;
        OnPropertyChanged(nameof(Status));
    }

    // Parsed as Validate reads them, so call these once it has passed.
    private int? ClearCount() => ClearKills.Trim().Length == 0 ? null : int.Parse(ClearKills.Trim(), NumberStyles.AllowThousands, CultureInfo.CurrentCulture);
    private int? EnemyCount() => EnemiesAtOnce.Trim().Length == 0 ? null : int.Parse(EnemiesAtOnce.Trim(), NumberStyles.None, CultureInfo.CurrentCulture);
    private int? KitCount() => ArmorKits.Trim().Length == 0 ? null : int.Parse(ArmorKits.Trim(), NumberStyles.None, CultureInfo.CurrentCulture);

    private void Check()
    {
        Invalidate(null);
        Problem = Validate(FightLength, Fights, SwapTime, Distance, pickupDelay: PickupDelay, pickupChance: PickupChance, clearKills: ClearKills,
            peekSeconds: PeekSeconds, coverSeconds: CoverSeconds, peekHitPercent: PeekHitPercent, incoming: Incoming, enemiesAtOnce: EnemiesAtOnce,
            armorKits: ArmorKits, armorKitAtPercent: ArmorKitAtPercent, focusSeconds: FocusSeconds, enemySpacing: EnemySpacing);
        if (Problem is not null) { return; }
        var cover = PeekSeconds.Trim().Length > 0;
        var enemies = EnemiesAtOnce.Trim().Length > 0;
        var kits = ArmorKits.Trim().Length > 0;
        var focus = FocusSeconds.Trim().Length > 0;
        var spaced = EnemySpacing.Trim().Length > 0;
        if (LoadoutResolver.EquipmentLimitProblem(session.Pack.Pack, session.Snapshot.State.Build) is { } equipmentProblem)
        { Problem = equipmentProblem.Message; return; }
        if (!ulong.TryParse(Seed.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) { Problem = "Seed must be a whole number from 0 to 18,446,744,073,709,551,615"; }
        else if (!SwapOptions.Contains(Swaps)) { Problem = "Choose a weapon swap mode"; }
        // A sequence needs random driven fights, so name it before the average fight's own advice.
        else if (!TargetSequence.IsEmpty && AverageFight) { Problem = "Mixed targets require random fights"; }
        else if (!TargetDistances.IsEmpty && AverageFight) { Problem = "Target distances require random fights"; }
        else if (cover && AverageFight) { Problem = "A cover cycle requires random fights"; }
        else if (enemies && AverageFight) { Problem = "Several enemies at once require random fights"; }
        else if (kits && AverageFight) { Problem = "Armor kits require random fights"; }
        else if (AverageFight && (DriveBuffs || Incoming)) { Problem = "Average fights require fixed assumptions and no incoming fire; turn off talent triggers and incoming fire"; }
        // The engine's own rules for a cover cycle and for several enemies.
        else if (cover && DummyFightOptions.CoverCycleProblem(DriveBuffs) is { } coverProblem) { Problem = coverProblem; }
        else if (enemies && DummyFightOptions.EnemiesAtOnceProblem(Incoming, DriveBuffs,
            ResolveAssumptions.Resolve(session.Pack.Pack, session.Snapshot.State.Assumptions).Numeric.GetValueOrDefault(DummyFightOptions.AttackersInput, 1)) is { } enemiesProblem) { Problem = enemiesProblem; }
        else if (EnemiesShrink && DummyFightOptions.EnemiesShrinkProblem(EnemyCount(), ClearCount()) is { } shrinkProblem) { Problem = shrinkProblem; }
        else if (kits && DummyFightOptions.ArmorKitsProblem(Incoming, DriveBuffs) is { } kitProblem) { Problem = kitProblem; }
        else if (focus && DummyFightOptions.FocusProblem(EnemyCount()) is { } focusProblem) { Problem = focusProblem; }
        else if (spaced && DummyFightOptions.EnemySpacingProblem(EnemyCount()) is { } spacingProblem) { Problem = spacingProblem; }
        else if (Swaps != NoSwaps && Incoming && !DriveBuffs) { Problem = SwapsUnderFire; }
        else if (Swaps != NoSwaps && SwapWeapon.Id.Length == 0) { Problem = "Equip and choose another weapon for swaps"; }
        else if (IncludeShield && !Incoming) { Problem = "Shield simulation requires incoming fire"; }
        else if (IncludeSkills && DriveBuffs && !Incoming) { Problem = "Driven damaging skills require incoming fire"; }
        else if (Distance.Trim().Length > 0 && !DriveBuffs) { Problem = "Target distance requires talent triggers"; }
        // The engine's own rule for a target sequence.
        else if (!TargetSequence.IsEmpty && DummyFightOptions.TargetSequenceProblem(DriveBuffs) is { } sequenceProblem)
        { Problem = sequenceProblem; }
        else if (!TargetDistances.IsEmpty && DummyFightOptions.TargetDistancesProblem(DriveBuffs) is { } distancesProblem) { Problem = distancesProblem; }
        else if (DistanceEntry.Trim().Length > 0) { Problem = "Add the typed target distance, or clear the box"; }
        // The talent rules offered follow the build and the form, even while another field has a problem.
        OfferSkillUseKeys();
        if (Problem is not null) { return; }
        // Each slot's "Use when", checked as the engine checks it.
        var build = session.Snapshot.State.Build;
        var damaging = session.Pack.Pack.Simulation.Skills.Select(rule => rule.Skill).ToHashSet(StringComparer.Ordinal);
        foreach (var use in SkillUses)
        {
            if (use.Problem(IncludeSkills, damaging.Contains(build.ActiveSkillSelections.GetValueOrDefault(use.Slot, "")), DriveBuffs, enemies ? EnemyCount() : null,
                CultureInfo.CurrentCulture) is { } useProblem) { Problem = useProblem; return; }
        }
    }

    // The keys of the rules the engine runs for this build and form (the weapon in hand, a swap's second weapon, full sets and
    // specialization nodes), each named with its talent, in the pack's order. The engine still has the last word when the fight runs.
    private void OfferSkillUseKeys()
    {
        if (SkillUses.IsDefaultOrEmpty) { return; }
        var pack = session.Pack;
        var snapshot = session.Snapshot;
        // Resolving the build is the costly part, so it's redone only when the build, the assumptions or these options change.
        var asked = (snapshot.State.Build, snapshot.State.Assumptions, DriveBuffs, Incoming, Swaps == NoSwaps ? "" : SwapWeapon.Id);
        if (asked.Equals(offeredFor)) { return; }
        offeredFor = asked;
        var talents = DummyWeaponSimulator.RuleTalents(pack, snapshot.State.Build, snapshot.State.Assumptions, new DummyFightOptions
        {
            DriveSimpleBuffs = DriveBuffs,
            IncludeIncoming = Incoming,
            Rotation = asked.Item5.Length == 0 ? null : new(asked.Item5)
        });
        var names = pack.Pack.Talents.ToDictionary(talent => talent.ID, talent => talent.Name, StringComparer.Ordinal);
        ImmutableArray<Choice> keys = [.. pack.Pack.Simulation.Triggers.Where(rule => talents.Contains(rule.Talent) && rule.Key.Length > 0)
            .Select(rule => new Choice(rule.Key, $"{names.GetValueOrDefault(rule.Talent, rule.Talent)}: {rule.Key}")).DistinctBy(choice => choice.Id)];
        foreach (var use in SkillUses) { use.Offer(keys); }
    }

    private void Run()
    {
        if (!RunCommand.CanExecute(null)) { return; }
        var snapshot = session.Snapshot;
        RefreshWeapons(snapshot);
        Check();
        if (Problem is not null) { return; }
        requestRevision = snapshot.Revision;
        var options = new DummyFightOptions
        {
            Duration = Number(FightLength),
            Iterations = int.Parse(Fights.Trim(), CultureInfo.CurrentCulture),
            Seed = ulong.Parse(Seed.Trim(), CultureInfo.InvariantCulture),
            CaptureFirstFightEvents = CaptureEvents,
            Mode = AverageFight ? SimulationMode.Average : SimulationMode.Random,
            DriveSimpleBuffs = DriveBuffs,
            IncludeIncoming = Incoming,
            IncludeSkills = IncludeSkills,
            IncludeShield = IncludeShield,
            Distance = Optional(Distance),
            PickupDelay = Number(PickupDelay),
            PickupChance = Number(PickupChance) / 100,
            ClearKills = ClearCount(),
            TargetSequence = [.. TargetSequence.Select(choice => int.Parse(choice.Id, CultureInfo.InvariantCulture))],
            TargetDistances = TargetDistances,
            PeekSeconds = Optional(PeekSeconds),
            CoverSeconds = Optional(CoverSeconds),
            PeekHitShare = Optional(PeekHitPercent) / 100,
            EnemiesAtOnce = EnemyCount(),
            EnemiesShrink = EnemiesShrink,
            FocusSeconds = Optional(FocusSeconds),
            EnemySpacing = Optional(EnemySpacing),
            // Hidden with skills left out, the rows play no part then.
            SkillUse = !IncludeSkills ? ImmutableDictionary<string, SkillUseCondition>.Empty : SkillUses.Select(use => (use.Slot, Condition: use.Condition(CultureInfo.CurrentCulture)))
                .Where(use => use.Condition is not null).ToImmutableDictionary(use => use.Slot, use => use.Condition!, StringComparer.Ordinal),
            ArmorKits = KitCount(),
            ArmorKitAtPercent = Optional(ArmorKitAtPercent),
            Rotation = Swaps == NoSwaps ? null : new(SwapWeapon.Id, Swaps == SwapAfterKill ? WeaponSwapMode.AfterKill : WeaponSwapMode.WhenEmpty,
                Number(SwapTime))
        };
        // Parsed trimmed, as Validate read them: Trim removes spaces double.Parse would refuse (a pasted no-break space).
        static double Number(string text) => double.Parse(text.Trim(), CultureInfo.CurrentCulture);
        static double? Optional(string text) => text.Trim().Length == 0 ? null : Number(text);
        Volatile.Write(ref observesSessionChanges, 1);
        IsRunning = true;
        message = "Running simulation…";
        OnPropertyChanged(nameof(Status));
        Running = simulation.Start(new(options));
    }

    private void ShowProgress(SimulationRunProgress update)
    {
        if (disposed || !IsRunning || !simulation.IsCurrent(update)) { return; }
        Progress = SimulationPresentation.ProgressFraction(update.Progress);
    }

    private void Show(JobResult<SessionSimulationResult> result)
    {
        if (disposed || !simulation.IsCurrent(result)) { return; }
        IsRunning = false;
        if (result.Error is { } error)
        {
            Invalidate(null);
            Volatile.Write(ref observesSessionChanges, 1);
            RunError = error is SimulationException failure ? $"{failure.Message} ({failure.Code})" : error.Message;
            return;
        }
        var fight = result.Value!.Fight;
        SetResult(result.Value);
        Timeline = SimulationTimeline.From(fight);
        Progress = 1;
        message = $"Completed {fight.Iterations:N0} fight(s), {fight.Duration:N2} s each · seed {fight.Seed}";
        ResultText = SimulationPresentation.Summary(fight, [.. SkillUses.Select(use => (use.Slot, use.Name))]);
        Limitations = string.Join(Environment.NewLine, fight.Limitations.Append(fight.Coverage.InputBasis).Append(fight.Coverage.Summary));
        OnPropertyChanged(nameof(Status));
    }

    private void SetResult(SessionSimulationResult? accepted)
    {
        var fight = accepted?.Fight;
        Distributions = SimulationDistributions.From(fight);
        IncomingReport = SimulationIncomingReport.From(fight?.Incoming);
        UptimeReport = accepted is null ? SimulationUptimeReport.Empty : SimulationUptimeReport.From(fight, accepted.Snapshot.Pack.Pack);
        EventReport = accepted is null ? SimulationEventReport.Empty : SimulationEventReport.From(fight,
            accepted.Request.Options.CaptureFirstFightEvents, accepted.Snapshot.Pack.Pack);
        Result = fight;
    }

    private bool RefreshWeapons(SessionSnapshot snapshot)
    {
        var active = snapshot.State.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
        if (ReferenceEquals(snapshot.State.Build.Equipment, seenEquipment) && active == seenActive) { return false; }
        Volatile.Write(ref seenEquipment, snapshot.State.Build.Equipment);
        Volatile.Write(ref seenActive, active);
        var choices = EquippedWeaponSlots.Read(snapshot.Pack.Pack, snapshot.State.Build)
            .Where(slot => slot.ID != active).Select(slot => new Choice(slot.ID, slot.Name)).ToImmutableArray();
        if (SwapWeapons.SequenceEqual(choices)) { return false; }
        SwapWeapons = choices;
        swapWeapon = SwapWeapons.FirstOrDefault(choice => choice.Id == swapWeapon.Id) ?? SwapWeapons.FirstOrDefault() ?? Choice.None;
        OnPropertyChanged(nameof(SwapWeapon));
        return true;
    }

    private static string Attackers(SessionSnapshot snapshot) => snapshot.State.Assumptions.GetValueOrDefault(DummyFightOptions.AttackersInput, "");

    private void OnChanged(SessionSnapshot snapshot)
    {
        var active = snapshot.State.Assumptions.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
        if (Volatile.Read(ref observesSessionChanges) == 0 && ReferenceEquals(snapshot.State.Build.Equipment, Volatile.Read(ref seenEquipment)) && active == Volatile.Read(ref seenActive) &&
            Attackers(snapshot) == Volatile.Read(ref seenAttackers)) { return; }
        if (Interlocked.Exchange(ref refreshPosted, 1) != 0) { return; }
        ui.Post(() =>
        {
            Volatile.Write(ref refreshPosted, 0);
            if (disposed) { return; }
            var current = session.Snapshot;
            var equipmentChanged = !ReferenceEquals(current.State.Build.Equipment, seenEquipment);
            var weaponsChanged = RefreshWeapons(current);
            // The form's check on several enemies reads this assumption, so with that field set a change can clear or raise its problem.
            var attackers = Attackers(current);
            var attackersChanged = attackers != seenAttackers && EnemiesAtOnce.Trim().Length > 0;
            Volatile.Write(ref seenAttackers, attackers);
            if (Volatile.Read(ref observesSessionChanges) != 0 && current.Revision != requestRevision)
            {
                Check();
                message = "Build changed; run the simulation again.";
                OnPropertyChanged(nameof(Status));
            }
            else if ((weaponsChanged || equipmentChanged || attackersChanged) && !IsRunning) { Check(); }
        });
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        session.Changed -= OnChanged;
        simulation.Dispose();
        IsRunning = false;
        RunCommand.NotifyCanExecuteChanged();
        foreach (var command in new[] { AddTargetCommand, RemoveLastTargetCommand, ClearTargetsCommand, AddDistanceCommand, RemoveLastDistanceCommand, ClearDistancesCommand })
        { command.NotifyCanExecuteChanged(); }
    }

    /// <summary>Go's simulationOptions checks: the first value out of range, or null when the form would run.</summary>
    public static string? Validate(string fightLength, string fights, string swapTime, string distance, IFormatProvider? culture = null,
        string pickupDelay = "2", string pickupChance = "100", string clearKills = "",
        string peekSeconds = "", string coverSeconds = "", string peekHitPercent = "", bool incoming = false, string enemiesAtOnce = "",
        string armorKits = "", string armorKitAtPercent = "", string focusSeconds = "", string enemySpacing = "")
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.TryParse(fightLength.Trim(), NumberStyles.Float, culture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > SimulationLimits.MaximumFightDuration)
        { return $"Fight length must be a number of seconds from 1 to {SimulationLimits.MaximumFightDuration:G}"; }
        if (!int.TryParse(fights.Trim(), NumberStyles.AllowLeadingSign, culture, out var count) || count < 1 || count > 10000)
        { return "Fights must be a whole number from 1 to 10,000"; }
        if (!double.TryParse(swapTime.Trim(), NumberStyles.Float, culture, out var swap) || !double.IsFinite(swap) || swap <= 0 || swap > 10)
        { return "Swap time must be a number of seconds above 0 and up to 10"; }
        if (distance.Trim().Length > 0 && ParseDistance(distance, culture) is null)
        { return "Target distance must be a number of meters above 0 and up to 100, or empty for none"; }
        if (!double.TryParse(pickupDelay.Trim(), NumberStyles.Float, culture, out var delay) || !double.IsFinite(delay) || delay < 0 || delay > 60)
        { return "Trophy pickup delay must be a number of seconds from 0 to 60"; }
        if (!double.TryParse(pickupChance.Trim(), NumberStyles.Float, culture, out var chance) || !double.IsFinite(chance) || chance < 0 || chance > 100)
        { return "Trophies collected must be a percentage from 0 to 100"; }
        if (clearKills.Trim() is { Length: > 0 } targets && (!int.TryParse(targets, NumberStyles.AllowThousands, culture, out var kills) || kills is < 1 or > 10000))
        { return "Targets to clear must be a whole number from 1 to 10,000, or empty for none"; }
        if (peekSeconds.Trim().Length > 0 && !InRange(peekSeconds, DummyFightOptions.MinimumCoverPhase, DummyFightOptions.MaximumCoverPhase, culture))
        { return string.Create(culture, $"Peek time must be a number of seconds from {DummyFightOptions.MinimumCoverPhase:G} to {DummyFightOptions.MaximumCoverPhase:G}, or empty for none"); }
        if (coverSeconds.Trim().Length > 0 && !InRange(coverSeconds, DummyFightOptions.MinimumCoverPhase, DummyFightOptions.MaximumCoverPhase, culture))
        { return string.Create(culture, $"Cover time must be a number of seconds from {DummyFightOptions.MinimumCoverPhase:G} to {DummyFightOptions.MaximumCoverPhase:G}, or empty for none"); }
        if (peekHitPercent.Trim().Length > 0 && (!double.TryParse(peekHitPercent.Trim(), NumberStyles.Float, culture, out var hit) || !double.IsFinite(hit) || hit < 0 || hit > 100))
        { return "Hit chance while peeking must be a percentage from 0 to 100, or empty for none"; }
        var peek = peekSeconds.Trim().Length > 0;
        if (peek != coverSeconds.Trim().Length > 0) { return "A cover cycle needs the peek time and the cover time"; }
        var peekHit = peekHitPercent.Trim().Length > 0;
        if (peekHit && !peek) { return "The hit chance while peeking needs a cover cycle: set the peek time and the cover time"; }
        if (incoming && peek && !peekHit) { return "A cover cycle under incoming fire needs the hit chance while peeking"; }
        if (enemiesAtOnce.Trim() is { Length: > 0 } group && (!int.TryParse(group, NumberStyles.None, culture, out var enemies) ||
            enemies is < DummyFightOptions.MinimumEnemiesAtOnce or > DummyFightOptions.MaximumEnemiesAtOnce))
        { return $"Enemies at once must be a whole number from {DummyFightOptions.MinimumEnemiesAtOnce} to {DummyFightOptions.MaximumEnemiesAtOnce}, or empty for one at a time"; }
        if (focusSeconds.Trim().Length > 0 && !InRange(focusSeconds, DummyFightOptions.MinimumFocusSeconds, DummyFightOptions.MaximumFocusSeconds, culture))
        { return string.Create(culture, $"Time on each enemy must be a number of seconds from {DummyFightOptions.MinimumFocusSeconds:G} to {DummyFightOptions.MaximumFocusSeconds:G}, or empty to shoot each until it dies"); }
        if (enemySpacing.Trim().Length > 0 && !InRange(enemySpacing, DummyFightOptions.MinimumEnemySpacing, DummyFightOptions.MaximumEnemySpacing, culture))
        { return string.Create(culture, $"Enemy spacing must be a number of meters from {DummyFightOptions.MinimumEnemySpacing:G} to {DummyFightOptions.MaximumEnemySpacing:G}, or empty for none"); }
        if (armorKits.Trim() is { Length: > 0 } carried && (!int.TryParse(carried, NumberStyles.None, culture, out var kits) ||
            kits is < DummyFightOptions.MinimumArmorKits or > DummyFightOptions.MaximumArmorKits))
        { return $"Armor kits must be a whole number from {DummyFightOptions.MinimumArmorKits} to {DummyFightOptions.MaximumArmorKits}, or empty for none"; }
        if (armorKitAtPercent.Trim().Length > 0 && !InRange(armorKitAtPercent, DummyFightOptions.MinimumArmorKitPercent, DummyFightOptions.MaximumArmorKitPercent, culture))
        { return string.Create(culture, $"Use a kit below must be a percentage from {DummyFightOptions.MinimumArmorKitPercent:G} to {DummyFightOptions.MaximumArmorKitPercent:G}, or empty for none"); }
        if (armorKits.Trim().Length > 0 != armorKitAtPercent.Trim().Length > 0) { return "Armor kits need the number of kits and the armor percentage"; }
        return null;
    }

    private static bool InRange(string text, double minimum, double maximum, IFormatProvider culture) =>
        double.TryParse(text.Trim(), NumberStyles.Float, culture, out var value) && double.IsFinite(value) && value >= minimum && value <= maximum;
}
