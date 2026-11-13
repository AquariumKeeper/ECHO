using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Simulation;

public sealed record RotationOptions(string SecondSlot, WeaponSwapMode Mode = WeaponSwapMode.WhenEmpty, double SwapTime = 1);

public enum SimulationMode { Average, Random }

public static class SimulationLimits
{
    public const double MaximumFightDuration = 600;
}

/// <summary>Options for the weapon-only dummy foundation, not the eventual full encounter simulator.</summary>
public sealed record DummyFightOptions
{
    public double Duration { get; init; } = 60;
    public int Iterations { get; init; } = 500;
    public ulong Seed { get; init; } = 1;
    public SimulationMode Mode { get; init; } = SimulationMode.Average;
    public bool CaptureFirstFightEvents { get; init; }
    public bool IncludeSkills { get; init; }
    public bool DriveSimpleBuffs { get; init; }
    public bool IncludeIncoming { get; init; }
    public bool IncludeShield { get; init; }
    public double? Distance { get; init; }
    public RotationOptions? Rotation { get; init; }
    /// <summary>Seconds from a kill until its dropped pickup (Kill Confirmed's trophy) is collected: an assumption about play.</summary>
    public double PickupDelay { get; init; } = 2;
    /// <summary>The chance each dropped pickup is collected at all; a missed one grants nothing.</summary>
    public double PickupChance { get; init; } = 1;
    /// <summary>
    /// With targets: report how long each fight takes to kill this many targets (P10, an encounter by kill count). Fights keep
    /// their full duration; the time is the Nth kill's.
    /// </summary>
    public int? ClearKills { get; init; }
    /// <summary>
    /// With targets: the fight's targets cycle through these enemy target profiles in order (P10, mixed targets; for example
    /// four normals then an elite). Each target has its own pools and target-dependent bonuses, skill damage included, and the
    /// target in play sets enemy fire and recovery. Empty for one profile throughout.
    /// </summary>
    public ImmutableArray<int> TargetSequence { get; init; } = [];
    /// <summary>
    /// With targets and driven triggers: the fight's targets cycle through these distances in metres (P10, several distances),
    /// independently of <see cref="TargetSequence"/>. Each target's distance sets its weapon falloff, its distance rules and any
    /// enemy fire that follows distance, in place of <see cref="Distance"/>. Empty for one distance throughout.
    /// </summary>
    public ImmutableArray<double> TargetDistances { get; init; } = [];
    /// <summary>
    /// With <see cref="CoverSeconds"/>: the player fights from cover (ADR 0009), peeking this many seconds to shoot in each cycle. The fight starts at a peek. Null for a fight without cover.
    /// </summary>
    public double? PeekSeconds { get; init; }
    /// <summary>Seconds fully behind cover in each cycle: no shooting, a reload when the magazine isn't full (not when swapping when
    /// empty), enemy fire blocked.</summary>
    public double? CoverSeconds { get; init; }
    /// <summary>The share of the normal enemy hit chance that still lands while peeking, from 0 to 1. A cover cycle under incoming
    /// fire needs it, and there is no default.</summary>
    public double? PeekHitShare { get; init; }
    /// <summary>
    /// With incoming fire and driven triggers: this many enemies fire at once (ADR 0008). The player shoots one at a time; the
    /// others wait and keep firing, each as its own profile, and the next joins as one dies. Null for one at a time.
    /// </summary>
    public int? EnemiesAtOnce { get; init; }
    /// <summary>
    /// With <see cref="EnemiesAtOnce"/>: there are <see cref="ClearKills"/> enemies in all (ADR 0008). The next joins until none are
    /// left, then fire falls with each kill; after the last nobody fires or is shot, and the fight runs out its length.
    /// </summary>
    public bool EnemiesShrink { get; init; }
    /// <summary>
    /// With <see cref="EnemiesAtOnce"/>: the seconds the player shoots an enemy before moving to the next living one in turn (ADR
    /// 0011), so enemies keep what's on them. There is no default: it's an assumption about play. Null shoots each until it dies.
    /// </summary>
    public double? FocusSeconds { get; init; }
    /// <summary>
    /// With <see cref="EnemiesAtOnce"/>: the metres between neighbouring enemies, standing on a line by their place in the group
    /// (ADR 0013), for effects that reach other enemies. There is no default; without it those effects find nobody.
    /// </summary>
    public double? EnemySpacing { get; init; }
    /// <summary>
    /// When each skill slot's skill is used once it's ready (ADR 0014), keyed by slot; a slot not named is used as soon as it's ready.
    /// The player's choice, not the game's.
    /// </summary>
    public ImmutableDictionary<string, SkillUseCondition> SkillUse { get; init; } = ImmutableDictionary<string, SkillUseCondition>.Empty;
    /// <summary>
    /// With <see cref="ArmorKitAtPercent"/>, incoming fire and driven triggers: the armor kits the player carries (ADR 0010). There
    /// is no default, and the pack supplies what a kit does. Null for a fight without kits.
    /// </summary>
    public int? ArmorKits { get; init; }
    /// <summary>The percentage of max armor below which the player uses a kit, while one is left. There is no default.</summary>
    public double? ArmorKitAtPercent { get; init; }

    /// <summary>The longest target sequence (or list of target distances) a fight takes.</summary>
    public const int MaximumTargetSequence = 100;
    /// <summary>The farthest fight or target distance, in metres.</summary>
    public const double MaximumDistance = 1000;
    /// <summary>The pack's numeric input that chooses the enemy target profile.</summary>
    public const string TargetProfileInput = "enemy_target";
    /// <summary>The pack's selector for being in cover, on for a whole fight with a cover cycle.</summary>
    public const string CoverInput = "in_cover";
    /// <summary>The shortest peek or cover phase, in seconds: shorter phases would spend the fight's work budget on boundaries.</summary>
    public const double MinimumCoverPhase = 0.1;
    /// <summary>The longest peek or cover phase, in seconds.</summary>
    public const double MaximumCoverPhase = 60;

    /// <summary>The shortest and the longest time on an enemy before moving on.</summary>
    public const double MinimumFocusSeconds = 0.1, MaximumFocusSeconds = 60;
    /// <summary>The closest and the farthest neighbouring enemies, in metres.</summary>
    public const double MinimumEnemySpacing = 0.5, MaximumEnemySpacing = 50;
    /// <summary>The fewest and the most armor kits a fight takes.</summary>
    public const int MinimumArmorKits = 1, MaximumArmorKits = 10;
    /// <summary>The lowest and the highest armor percentage a kit may wait for.</summary>
    public const double MinimumArmorKitPercent = 1, MaximumArmorKitPercent = 100;
    /// <summary>The fewest and the most enemies that fire at once.</summary>
    public const int MinimumEnemiesAtOnce = 2, MaximumEnemiesAtOnce = 10;
    /// <summary>The pack's numeric input that multiplies one target's fire.</summary>
    public const string AttackersInput = "enemy_attackers";

    /// <summary>Whether the fight runs a cover cycle; its two times come together.</summary>
    public bool HasCoverCycle => PeekSeconds is not null;

    /// <summary>Why a fight can't run a cover cycle, or null when it can. Shared by the engine and the desktop form.</summary>
    public static string? CoverCycleProblem(bool driven) => driven ? null : "A cover cycle requires talent triggers";

    /// <summary>
    /// Why a fight with these settings can't run a target sequence, or null when it can: the profile rides on the driven
    /// state. The engine and the desktop form share it; the engine also refuses a sequence whose profiles change skill timing.
    /// </summary>
    public static string? TargetSequenceProblem(bool driven) => driven ? null : "Mixed targets require talent triggers";

    /// <summary>Why a fight can't run target distances, or null when it can: they ride on the driven state. Shared like the
    /// sequence rule.</summary>
    public static string? TargetDistancesProblem(bool driven) => driven ? null : "Target distances require talent triggers";

    /// <summary>Why a fight can't have several enemies firing at once, or null when it can. Shared by the engine and the desktop
    /// form. The attackers input multiplies one target's fire, so above 1 it would count them twice.</summary>
    public static string? EnemiesAtOnceProblem(bool incoming, bool driven, double attackers) =>
        !incoming ? "Several enemies at once require incoming fire" : !driven ? "Several enemies at once require talent triggers" :
        attackers > 1 ? "Several enemies at once replace Enemies Attacking; set that to 1" : null;

    /// <summary>Why a fight can't use armor kits, or null when it can. Shared by the engine and the desktop form; the engine also
    /// needs the pack's kit, and refuses builds whose kit the pack says is replaced.</summary>
    public static string? ArmorKitsProblem(bool incoming, bool driven) =>
        !incoming ? "Armor kits require incoming fire" : !driven ? "Armor kits require talent triggers" : null;

    /// <summary>Why a fight can't move between enemies, or null when it can. Shared by the engine and the desktop form.</summary>
    public static string? FocusProblem(int? enemies) => enemies is null ? "Time on each enemy needs several enemies at once" : null;

    /// <summary>Why a fight can't take an enemy spacing, or null when it can.</summary>
    public static string? EnemySpacingProblem(int? enemies) => enemies is null ? "Enemy spacing needs several enemies at once" : null;

    /// <summary>Why a fight's group can't shrink, or null when it can. Shared by the engine and the desktop form.</summary>
    public static string? EnemiesShrinkProblem(int? enemies, int? clearKills) =>
        enemies is null ? "A shrinking group needs several enemies at once" :
        clearKills is null ? "A shrinking group needs a number of targets to clear: how many enemies there are in all" :
        clearKills < enemies ? "A shrinking group needs at least as many targets to clear as enemies at once" : null;

    internal void Validate()
    {
        if (!double.IsFinite(Duration) || Duration <= 0 || Duration > SimulationLimits.MaximumFightDuration) { throw new ArgumentOutOfRangeException(nameof(Duration)); }
        if (Iterations is < 1 or > 10000) { throw new ArgumentOutOfRangeException(nameof(Iterations)); }
        if (!Enum.IsDefined(Mode)) { throw new ArgumentOutOfRangeException(nameof(Mode)); }
        if (Distance is { } distance && (!double.IsFinite(distance) || distance < 0 || distance > MaximumDistance))
        { throw new ArgumentOutOfRangeException(nameof(Distance)); }
        if (!double.IsFinite(PickupDelay) || PickupDelay < 0 || PickupDelay > 60) { throw new ArgumentOutOfRangeException(nameof(PickupDelay)); }
        if (!double.IsFinite(PickupChance) || PickupChance < 0 || PickupChance > 1) { throw new ArgumentOutOfRangeException(nameof(PickupChance)); }
        if (ClearKills is < 1 or > 10000) { throw new ArgumentOutOfRangeException(nameof(ClearKills)); }
        if (!TargetSequence.IsDefault && TargetSequence.Length > MaximumTargetSequence) { throw new ArgumentOutOfRangeException(nameof(TargetSequence)); }
        if (!TargetDistances.IsDefault && (TargetDistances.Length > MaximumTargetSequence || TargetDistances.Any(meters => !double.IsFinite(meters) || meters < 0 || meters > MaximumDistance)))
        { throw new ArgumentOutOfRangeException(nameof(TargetDistances)); }
        if ((PeekSeconds is null) != (CoverSeconds is null))
        { throw new ArgumentException("A cover cycle needs the peek time and the cover time together", nameof(PeekSeconds)); }
        if (PeekHitShare is not null && PeekSeconds is null)
        { throw new ArgumentException("The peek hit share needs a cover cycle", nameof(PeekHitShare)); }
        if (IncludeIncoming && PeekSeconds is not null && PeekHitShare is null)
        { throw new ArgumentException("A cover cycle under incoming fire needs the peek hit share", nameof(PeekHitShare)); }
        if (PeekSeconds is { } peek && (!double.IsFinite(peek) || peek < MinimumCoverPhase || peek > MaximumCoverPhase)) { throw new ArgumentOutOfRangeException(nameof(PeekSeconds)); }
        if (CoverSeconds is { } cover && (!double.IsFinite(cover) || cover < MinimumCoverPhase || cover > MaximumCoverPhase)) { throw new ArgumentOutOfRangeException(nameof(CoverSeconds)); }
        if (PeekHitShare is { } share && (!double.IsFinite(share) || share < 0 || share > 1)) { throw new ArgumentOutOfRangeException(nameof(PeekHitShare)); }
        if ((ArmorKits is null) != (ArmorKitAtPercent is null))
        { throw new ArgumentException("Armor kits need the number of kits and the armor percentage together", nameof(ArmorKits)); }
        if (ArmorKits is < MinimumArmorKits or > MaximumArmorKits) { throw new ArgumentOutOfRangeException(nameof(ArmorKits)); }
        if (ArmorKitAtPercent is { } kitPercent && (!double.IsFinite(kitPercent) || kitPercent < MinimumArmorKitPercent || kitPercent > MaximumArmorKitPercent)) { throw new ArgumentOutOfRangeException(nameof(ArmorKitAtPercent)); }
        if (EnemiesAtOnce is < MinimumEnemiesAtOnce or > MaximumEnemiesAtOnce) { throw new ArgumentOutOfRangeException(nameof(EnemiesAtOnce)); }
        if (FocusSeconds is { } focus && (!double.IsFinite(focus) || focus < MinimumFocusSeconds || focus > MaximumFocusSeconds)) { throw new ArgumentOutOfRangeException(nameof(FocusSeconds)); }
        if (EnemySpacing is { } spacing && (!double.IsFinite(spacing) || spacing < MinimumEnemySpacing || spacing > MaximumEnemySpacing)) { throw new ArgumentOutOfRangeException(nameof(EnemySpacing)); }
        if (SkillUse is null) { throw new ArgumentNullException(nameof(SkillUse)); }
        foreach (var (slot, condition) in SkillUse)
        {
            if (string.IsNullOrEmpty(slot) || condition is null || condition.Problem() is not null) { throw new ArgumentException(condition?.Problem() ?? "A skill use needs a slot and a condition", nameof(SkillUse)); }
        }
    }
}

public enum DummyEventKind { Shot, ReloadStarted, ReloadCompleted, Kill, TargetArrived, SwapStarted, SwapCompleted, SkillImpact, SkillTick, EnemyShot, EnemyHit, ArmorBreak, Downed, EnemyShotBlocked, ArmorRepaired, HealthRepaired, ShieldRaised, ShieldHit, ShieldBreak, ShieldRepaired, TargetMarked, PreservationStarted, BurnTick, BleedTick, BonusArmorGranted, Pickup, TalentTick, ArmorKitStarted, ArmorKitUsed, TalentBlast, DebuffPassedOn, StatusPassedOn, EnemyMarked, DamageRedirected, TrapSprung, SkillAreaHit }
public sealed record DummyEvent(double Time, DummyEventKind Kind, double Damage = 0, string WeaponSlot = "");
public static class DummyEventAmounts
{
    /// <summary>Amount-bearing event kinds include numeric zero; other events carry no amount.</summary>
    public static bool CarriesAmount(this DummyEventKind kind) => kind is DummyEventKind.Shot or DummyEventKind.SkillImpact
        or DummyEventKind.SkillTick or DummyEventKind.EnemyHit or DummyEventKind.ShieldHit or DummyEventKind.ShieldRepaired
        or DummyEventKind.ArmorRepaired or DummyEventKind.HealthRepaired or DummyEventKind.PreservationStarted
        or DummyEventKind.BurnTick or DummyEventKind.BleedTick or DummyEventKind.BonusArmorGranted or DummyEventKind.TalentTick or DummyEventKind.ArmorKitUsed or DummyEventKind.TalentBlast or DummyEventKind.DebuffPassedOn or DummyEventKind.StatusPassedOn or DummyEventKind.DamageRedirected or DummyEventKind.TrapSprung or DummyEventKind.SkillAreaHit;
}
public sealed record SimulationProgress(int CompletedFights, int TotalFights);
public sealed record FightSummary(double Mean, double StdDev, double Min, double P5, double P50, double P95, double Max);
/// <summary>
/// ADR 0014's condition for a skill slot: <c>ready</c> uses it as soon as it's ready; <c>key</c> waits for a driven key to be on;
/// <c>afterSeconds</c> waits for the fight to have run <see cref="Seconds"/>; <c>enemiesAtLeast</c> waits for at least
/// <see cref="Count"/> enemies to be alive in the group.
/// </summary>
public sealed record SkillUseCondition
{
    public string When { get; init; } = "ready";
    public string Key { get; init; } = "";
    public double Seconds { get; init; }
    public int Count { get; init; }
    public const string Ready = "ready", KeyOn = "key", AfterSeconds = "afterSeconds", EnemiesAtLeast = "enemiesAtLeast";

    /// <summary>What's wrong with the condition, or null.</summary>
    public string? Problem() => When switch
    {
        Ready => Key.Length > 0 || Seconds != 0 || Count != 0 ? "A ready skill use takes no key, seconds or count" : null,
        KeyOn => Key.Length == 0 || Seconds != 0 || Count != 0 ? "A key skill use needs a key and takes no seconds or count" : null,
        AfterSeconds => Key.Length > 0 || Count != 0 || !double.IsFinite(Seconds) || Seconds <= 0 || Seconds > SimulationLimits.MaximumFightDuration
            ? $"An afterSeconds skill use needs seconds above 0 up to {SimulationLimits.MaximumFightDuration:G}, and no key or count" : null,
        EnemiesAtLeast => Key.Length > 0 || Seconds != 0 || Count is < 1 or > DummyFightOptions.MaximumEnemiesAtOnce
            ? $"An enemiesAtLeast skill use needs a count from 1 to {DummyFightOptions.MaximumEnemiesAtOnce}, and no key or seconds" : null,
        _ => "A skill use is ready, key, afterSeconds or enemiesAtLeast"
    };

    public override string ToString() => When switch
    {
        KeyOn => $"while {Key} is on",
        AfterSeconds => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"after {Seconds:G} s"),
        EnemiesAtLeast => Count == 1 ? "while at least 1 enemy is alive" : $"while at least {Count} enemies are alive",
        _ => "when ready"
    };
}

/// <summary>A skill slot's mean uses per fight, and the mean time it was ready but held for its condition (ADR 0014).</summary>
public sealed record SkillSlotUse(double Uses, double HeldSeconds);

/// <summary>Grouped total, deployment-impact and modeled continuous-window skill DPS.</summary>
public sealed record SkillDpsSummary(FightSummary Total, FightSummary Impact, FightSummary ContinuousWindow);
/// <summary>Mean seconds in one phase/reading value and its fraction of the full requested duration.</summary>
public sealed record PhaseUptime(double Seconds, double Fraction);
/// <summary>Mean time a timed key is true/nonzero and, for stacks, mean live count over the full fight.</summary>
public sealed record TimedTriggerUptime(double Seconds, double Fraction, double? MeanStacks);
/// <summary>Mean active seconds per fight and fraction of the full requested fight duration.</summary>
public sealed record StatusUptime(double Seconds, double Fraction);
public sealed record IncomingFightResult(double Attempts, double Hits, double DamageTaken, double ArmorBreaks, double DownedFraction,
    FightSummary? TimeToFirstArmorBreak, FightSummary? TimeToDown)
{
    public double BlockedShots { get; init; }
    public double ShieldAbsorbed { get; init; }
    public double ShieldBreaks { get; init; }
    public double ShieldDeployments { get; init; }
    public double ShieldRepaired { get; init; }
    /// <summary>Swaps forced by a shield that allows only one rotated weapon: to it when raised, back after a break.</summary>
    public double ShieldSwitches { get; init; }
    public double ArmorRepaired { get; init; }
    public double HealthRepaired { get; init; }
    public double ArmorKitsUsed { get; init; }
    /// <summary>Kit uses the player went down during: the weapon stopped for them, but no repair landed and they aren't in <see cref="ArmorKitsUsed"/>.</summary>
    public double ArmorKitsCancelled { get; init; }
    /// <summary>The part of <see cref="ArmorRepaired"/> that armor kits repaired.</summary>
    public double ArmorKitRepaired { get; init; }
}

/// <summary>Timeline entries are mean damage in each one-second bin (the last bin may be shorter), not DPS.</summary>
public sealed record DummyFightResult(
    string Contract, SimulationMode Mode, int Iterations, double Duration, ulong Seed,
    FightSummary WeaponDps, double ShotsFired, double Reloads,
    ImmutableArray<double> Timeline, ImmutableArray<DummyEvent> FirstFightEvents,
    bool TraceTruncated, ImmutableArray<BuildDiagnostic> Diagnostics, ImmutableArray<string> Limitations)
{
    public EffectCoverageReport Coverage { get; init; } = EffectCoverageReport.Empty;
    public SkillDpsSummary? SkillDps { get; init; }
    /// <summary>Each conditioned skill slot's uses and held time (ADR 0014); empty in fights without <see cref="DummyFightOptions.SkillUse"/>.</summary>
    public ImmutableDictionary<string, SkillSlotUse> SkillUses { get; init; } = ImmutableDictionary<string, SkillSlotUse>.Empty;
    /// <summary>Burn and bleed tick damage on targets, when the pack defines status damage and a selected talent applies it.</summary>
    public FightSummary? StatusDps { get; init; }
    /// <summary>DPS of talent debuffs (Toxic Delivery), when a selected talent has one; part of the total, not of status DPS.</summary>
    public FightSummary? TalentDps { get; init; }
    public FightSummary? TotalDps { get; init; }
    public double Swaps { get; init; }
    public double Kills { get; init; }
    public ImmutableArray<double> KillTimes { get; init; } = [];
    /// <summary>Seconds from the fight's start, when the first target arrives, to its Nth kill within the fight
    /// (<see cref="DummyFightOptions.ClearKills"/>), over the fights that got there; null when none did or no count was asked for.</summary>
    public FightSummary? TimeToClear { get; init; }
    /// <summary>The share of fights that killed <see cref="DummyFightOptions.ClearKills"/> targets before the end.</summary>
    public double? ClearedFraction { get; init; }
    /// <summary>
    /// Each fight's average seconds from a target's arrival to its death, summarised over the fights with a kill (P15: time to
    /// kill with the fight's headshots, falloff, cover factor and resistances). Fights are weighted alike, a target still
    /// alive at the end isn't counted, and the time includes the fire interval or reload carried over from the previous
    /// target. Null without targets or kills; <see cref="KilledFraction"/> says how many fights had one.
    /// </summary>
    public FightSummary? TimeToKill { get; init; }
    /// <summary>With targets, the share of fights that killed at least one target before the end; null without targets.</summary>
    public double? KilledFraction { get; init; }
    public IncomingFightResult? Incoming { get; init; }
    public ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>> MagazinePhaseUptime { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>> CyclePhaseUptime { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>>.Empty;
    public ImmutableDictionary<string, TimedTriggerUptime> TimedTriggerUptime { get; init; } = ImmutableDictionary<string, TimedTriggerUptime>.Empty;
    public ImmutableDictionary<string, StatusUptime> TargetStatusUptime { get; init; } = ImmutableDictionary<string, StatusUptime>.Empty;
}

public sealed class SimulationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
