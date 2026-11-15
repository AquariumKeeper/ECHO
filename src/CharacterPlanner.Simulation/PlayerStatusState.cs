namespace CharacterPlanner.Simulation;

/// <summary>
/// Statuses enemies' hits put on the player (ADR 0018). An application while a status is off starts it, with its tick schedule at
/// the application (tick on apply) or one period later and its tick amount fixed. One while it's on extends it to the later end and
/// keeps the amount and cadence, as statuses on enemies do (R61). Ticks land strictly before the end. Ties tick in the order the
/// statuses started. Uptime is the time each status was on, up to the fight's end or the player going down.
/// </summary>
public sealed class PlayerStatusState
{
    // Each tick is worked out from the first as first + n x period, so adding periods doesn't drift.
    private sealed class Running(string status, double start, double until, double amount, double period, double firstTick)
    {
        public string Status { get; } = status;
        public double Start { get; } = start;
        public double Until { get; set; } = until;
        public double Amount { get; } = amount;
        public double Period { get; } = period;
        public double FirstTick { get; } = firstTick;
        public int Taken { get; set; }
        public double NextTick => FirstTick + Taken * Period;
    }

    private readonly List<Running> running = [];
    private readonly Dictionary<string, double> seconds = new(StringComparer.Ordinal);

    /// <summary>The time each status has been on, counted up to each one's end or the last <see cref="Close"/>.</summary>
    public IReadOnlyDictionary<string, double> Seconds => seconds;

    /// <summary>The next tick due, or null.</summary>
    public double? NextTick
    {
        get
        {
            double? next = null;
            foreach (var entry in running)
            {
                if (entry.Amount > 0 && entry.NextTick < entry.Until && (next is null || entry.NextTick < next)) { next = entry.NextTick; }
            }
            return next;
        }
    }

    /// <summary>Whether <paramref name="status"/> (or with "any", some status) is on the player at <paramref name="time"/>.</summary>
    public bool IsOn(string status, double time)
    {
        foreach (var entry in running)
        {
            if (entry.Start <= time && time < entry.Until && (status == "any" || entry.Status == status)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Puts <paramref name="status"/> on for <paramref name="duration"/> seconds from <paramref name="time"/>. A tick amount of 0 deals
    /// nothing; a positive one needs a positive period.
    /// </summary>
    public void Apply(string status, double time, double duration, double amount, double period, bool tickOnApply)
    {
        if (!double.IsFinite(time) || time < 0 || !double.IsFinite(duration) || duration <= 0 || !double.IsFinite(amount) || amount < 0 ||
            amount > 0 && (!double.IsFinite(period) || period <= 0))
        { throw new SimulationException("enemy-status-invalid", "A status on the player needs a finite time, a positive duration and, to tick, a positive period"); }
        var until = time + duration;
        var index = running.FindIndex(entry => entry.Status == status);
        if (index >= 0 && running[index].Until > time)
        {
            running[index].Until = Math.Max(running[index].Until, until);
            return;
        }
        if (index >= 0)
        {
            Count(running[index], running[index].Until);
            running.RemoveAt(index);
        }
        running.Add(new(status, time, until, amount, period, tickOnApply ? time : time + period));
    }

    /// <summary>Takes the tick due at <paramref name="time"/> and advances its schedule by one period.</summary>
    public (string Status, double Damage) Take(double time)
    {
        Running? due = null;
        foreach (var entry in running)
        {
            if (entry.Amount > 0 && entry.NextTick == time && time < entry.Until) { due = entry; break; }
        }
        if (due is null) { throw new SimulationException("time-not-advancing", "No status tick on the player is due then"); }
        var taken = due.NextTick;
        due.Taken++;
        if (!(due.NextTick > taken)) { throw new SimulationException("time-not-advancing", "A status tick on the player can't advance time"); }
        return (due.Status, due.Amount);
    }

    /// <summary>Counts every status's time on up to <paramref name="time"/>, the fight's end or the moment the player went down.</summary>
    public void Close(double time)
    {
        foreach (var entry in running) { Count(entry, Math.Min(entry.Until, time)); }
        running.Clear();
    }

    private void Count(Running entry, double end)
    {
        var on = Math.Max(0, end - entry.Start);
        if (on > 0) { seconds[entry.Status] = seconds.GetValueOrDefault(entry.Status) + on; }
    }
}
