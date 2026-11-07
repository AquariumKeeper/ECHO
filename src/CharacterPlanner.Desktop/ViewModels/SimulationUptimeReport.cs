using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record SimulationUptimeRow(string Name, string Value, double Seconds, double Fraction, double? MeanStacks = null)
{
    public string Label => Value.Length == 0 ? Name : $"{Name} · {Value}";
    public string Time => Seconds.ToString("N2");
    public string Share => Fraction.ToString("P1");
    public string Stacks => MeanStacks?.ToString("N2") ?? "—";
}

public sealed record SimulationUptimeSection(string Name, string Note, ImmutableArray<SimulationUptimeRow> Rows);

/// <summary>Projects measured engine state; absent readings are never synthesized from assumptions.</summary>
public sealed record SimulationUptimeReport(bool HasResult, ImmutableArray<SimulationUptimeSection> Sections, string Note)
{
    public static SimulationUptimeReport Empty { get; } = new(false, [], "");
    public string EmptyMessage => HasResult && Sections.IsEmpty ? "No state measurements were reported for this run. Fixed assumptions do not produce driven-state measurements." : "";

    public static SimulationUptimeReport From(DummyFightResult? result, GamePack pack)
    {
        if (result is null) { return Empty; }
        var selectors = pack.Selectors.ToDictionary(input => input.ID, StringComparer.Ordinal);
        var numeric = pack.NumericContextInputs.ToDictionary(input => input.ID, StringComparer.Ordinal);
        var options = numeric.ToDictionary(pair => pair.Key, pair => pair.Value.Options.ToDictionary(option => option.Value, option => option.Label), StringComparer.Ordinal);
        var sections = ImmutableArray.CreateBuilder<SimulationUptimeSection>(4);
        Add("Target statuses", "Active time on living targets. Any status is the union of statuses; overlapping status percentages must not be added.",
            [.. result.TargetStatusUptime.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new SimulationUptimeRow(pair.Key == "any" ? "Any status" : AssumptionViewModel.Humanize(pair.Key), "", pair.Value.Seconds, pair.Value.Fraction))]);
        Add("Timed conditions and stacks", "Boolean readings measure when the named condition is true, including readiness and lockout conditions. Stack readings measure time above zero; mean stacks include zero-stack time.",
            [.. result.TimedTriggerUptime.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new SimulationUptimeRow(Name(pair.Key), "", pair.Value.Seconds, pair.Value.Fraction, pair.Value.MeanStacks))]);
        Add("Magazine readings", "Time at each magazine value, including reloads. These are time shares, not fractions of shots.", Phases(result.MagazinePhaseUptime));
        Add("Cycle readings", "Time at each cycle value.", Phases(result.CyclePhaseUptime));
        return new(true, sections.ToImmutable(), "Seconds are means per fight. Percentages and mean stacks use the full requested fight duration. Measurement stops at downing, so time shares can total less than 100%. Only reported readings are listed; — means mean stacks is not applicable.");

        void Add(string name, string note, ImmutableArray<SimulationUptimeRow> rows)
        { if (!rows.IsEmpty) { sections.Add(new(name, note, rows)); } }

        string Name(string key)
        {
            // A talent on both swapped weapons reports each weapon's copy as key@slot.
            if (key.LastIndexOf('@') is var at and > 0 && pack.Slots.FirstOrDefault(slot => slot.ID == key[(at + 1)..]) is { } owner)
            { return $"{Name(key[..at])} ({owner.Name})"; }
            var name = selectors.GetValueOrDefault(key)?.Name ?? numeric.GetValueOrDefault(key)?.Name;
            return string.IsNullOrWhiteSpace(name) || name == key ? AssumptionViewModel.Humanize(key) : name;
        }

        string Value(string key, string value)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            {
                var label = options.GetValueOrDefault(key)?.GetValueOrDefault(number);
                return string.IsNullOrWhiteSpace(label) ? number.ToString("G", CultureInfo.CurrentCulture) : label;
            }
            return AssumptionViewModel.Humanize(value);
        }

        ImmutableArray<SimulationUptimeRow> Phases(ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>> readings) =>
            [.. readings.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair =>
            {
                var name = Name(pair.Key);
                return pair.Value.OrderBy(value => NumericOrder(value.Key)).ThenBy(value => value.Key, StringComparer.Ordinal)
                    .Select(value => new SimulationUptimeRow(name, Value(pair.Key, value.Key), value.Value.Seconds, value.Value.Fraction));
            })];
    }

    private static double NumericOrder(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : double.PositiveInfinity;
}
