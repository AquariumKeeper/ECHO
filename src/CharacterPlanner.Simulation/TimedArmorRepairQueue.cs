namespace CharacterPlanner.Simulation;

/// <summary>Independent constant-rate armor budgets. The caller integrates Rate before advancing to an expiry.</summary>
public sealed class TimedArmorRepairQueue
{
    private const int Limit = 1000000;
    private readonly PriorityQueue<double, double> repairs = new();
    private double clock;
    private double sum;
    private double correction;
    public double Rate => Math.Max(0, sum + correction);
    public double? NextExpiry => repairs.TryPeek(out _, out var until) ? until : null;

    /// <summary>Snapshot a budget at the current queue clock. Unused delivery at full armor is never retained.</summary>
    public void Enqueue(double amount, double duration, double time)
    {
        if (!double.IsFinite(time) || time != clock) { throw new ArgumentOutOfRangeException(nameof(time), "Advance the queue before adding a budget"); }
        if (!double.IsFinite(amount) || amount < 0 || !double.IsFinite(duration) || duration <= 0 || duration > 600)
        { throw new ArgumentOutOfRangeException(nameof(amount)); }
        var until = time + duration;
        var rate = amount / duration;
        if (!double.IsFinite(until) || until <= time || !double.IsFinite(rate))
        { throw new SimulationException("repair-rate-invalid", "Queued repair rate or expiry cannot be represented"); }
        if (amount > 0 && rate == 0) { throw new SimulationException("repair-rate-invalid", "Queued repair rate underflowed"); }
        if (amount == 0) { return; }
        if (repairs.Count == Limit) { throw new SimulationException("repair-queue-limit", "Too many pending armor repairs"); }
        var next = Add(sum, correction, rate);
        if (!double.IsFinite(next.Sum + next.Correction)) { throw new SimulationException("repair-overflow", "Queued armor repair rate overflowed"); }
        repairs.Enqueue(rate, until);
        (sum, correction) = next;
    }

    /// <summary>Expire completed budgets, discarding elapsed delivery; never apply this after integrating a future rate.</summary>
    public void Advance(double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time)); }
        while (repairs.TryPeek(out var rate, out var until) && until <= time)
        {
            repairs.Dequeue();
            (sum, correction) = Add(sum, correction, -rate);
        }
        if (repairs.Count == 0) { sum = correction = 0; }
        clock = time;
    }

    // Compensate both additions and removals so a small surviving budget is not lost
    // when a much larger overlapping rate expires.
    private static (double Sum, double Correction) Add(double sum, double correction, double value)
    {
        var next = sum + value;
        correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
        return (next, correction);
    }
}
