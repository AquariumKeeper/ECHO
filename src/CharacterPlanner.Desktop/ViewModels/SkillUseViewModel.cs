using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// One skill slot's "Use when" on the Simulate tab (ADR 0014): as soon as it's ready, while a talent rule's key is on, after a time
/// into the fight, or while at least a number of enemies are alive. The player's choice, not the game's.
/// </summary>
public sealed class SkillUseViewModel : ViewModelBase
{
    public const string WhenReady = "When ready";
    public const string WhileKey = "While a talent rule is on";
    public const string AfterTime = "After a time (s)";
    public const string WhileEnemies = "While at least N enemies are alive";
    public static ImmutableArray<string> Whens { get; } = [WhenReady, WhileKey, AfterTime, WhileEnemies];

    private readonly Action changed;
    private string when = WhenReady;
    // The seconds and the count are kept apart, so switching between them never reads one as the other.
    private string seconds = "", count = "";
    private Choice key = Choice.None;

    public SkillUseViewModel(string slot, string name, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Slot = slot;
        Name = name;
        this.changed = changed;
    }

    public string Slot { get; }
    public string Name { get; }
    public string Label => $"Use {Name} when";
    public ImmutableArray<string> WhenChoices => Whens;

    public string When
    {
        get => when;
        set
        {
            if (value is null || !Whens.Contains(value) || !SetProperty(ref when, value)) { return; }
            OnPropertyChanged(nameof(TakesValue));
            OnPropertyChanged(nameof(TakesKey));
            OnPropertyChanged(nameof(ValueHint));
            OnPropertyChanged(nameof(Value));
            changed();
        }
    }

    public string Value
    {
        get => When == WhileEnemies ? count : seconds;
        set
        {
            var text = value ?? "";
            if (Value == text) { return; }
            if (When == WhileEnemies) { count = text; } else { seconds = text; }
            OnPropertyChanged();
            changed();
        }
    }
    public bool TakesValue => When is AfterTime or WhileEnemies;
    public bool TakesKey => When == WhileKey;
    public string ValueHint => When == AfterTime ? "seconds" : "enemies";

    /// <summary>The keys the build's selected talents' rules drive, named with their talent.</summary>
    public ImmutableArray<Choice> Keys { get; private set => SetProperty(ref field, value); } = [];

    public Choice Key { get => key; set { if (value is not null && SetProperty(ref key, value)) { changed(); } } }

    /// <summary>
    /// Lists the keys a held skill can wait for. The chosen one stays while it's listed; one that's gone clears to None, so the form asks
    /// again rather than waiting on a rule nobody chose. With none chosen yet, the first is offered.
    /// </summary>
    internal void Offer(ImmutableArray<Choice> keys)
    {
        if (Keys.SequenceEqual(keys)) { return; }
        Keys = keys;
        key = keys.FirstOrDefault(choice => choice.Id == key.Id) ?? (key.Id.Length == 0 ? keys.FirstOrDefault() : null) ?? Choice.None;
        OnPropertyChanged(nameof(Key));
    }

    /// <summary>What's wrong with this slot's choice for a fight with these settings, or null.</summary>
    internal string? Problem(bool includeSkills, bool hasSkill, bool driveBuffs, int? enemiesAtOnce, IFormatProvider culture)
    {
        // With skills left out the rows are hidden and play no part.
        if (When == WhenReady || !includeSkills) { return null; }
        if (!hasSkill) { return $"{Name} has no damaging skill to hold"; }
        switch (When)
        {
            case WhileKey:
                if (!driveBuffs) { return $"{Name} waiting for a talent rule needs talent triggers"; }
                if (Key.Id.Length == 0) { return $"Choose the talent rule {Name} waits for"; }
                return null;
            case AfterTime:
                return Seconds(culture) is null ? string.Create(culture, $"{Name}'s time must be a number of seconds above 0 up to {SimulationLimits.MaximumFightDuration:G}") : null;
            default:
                if (Count(culture) is not { } enemies) { return $"{Name}'s enemies must be a whole number from 1 to {DummyFightOptions.MaximumEnemiesAtOnce}"; }
                if (enemiesAtOnce is not { } atOnce) { return $"{Name} waiting for enemies needs Enemies at once"; }
                return enemies > atOnce ? $"{Name} can't wait for more enemies than Enemies at once" : null;
        }
    }

    /// <summary>The condition, once <see cref="Problem"/> has passed; null for "When ready".</summary>
    internal SkillUseCondition? Condition(IFormatProvider culture) => When switch
    {
        WhileKey => new() { When = SkillUseCondition.KeyOn, Key = Key.Id },
        AfterTime => new() { When = SkillUseCondition.AfterSeconds, Seconds = Seconds(culture)!.Value },
        WhileEnemies => new() { When = SkillUseCondition.EnemiesAtLeast, Count = Count(culture)!.Value },
        _ => null
    };

    // Trimmed, as the other fields read: a pasted no-break space isn't a number.
    private double? Seconds(IFormatProvider culture) =>
        double.TryParse(seconds.Trim(), NumberStyles.Float, culture, out var value) && double.IsFinite(value) && value > 0 && value <= SimulationLimits.MaximumFightDuration ? value : null;

    private int? Count(IFormatProvider culture) =>
        int.TryParse(count.Trim(), NumberStyles.None, culture, out var value) && value is >= 1 and <= DummyFightOptions.MaximumEnemiesAtOnce ? value : null;
}
