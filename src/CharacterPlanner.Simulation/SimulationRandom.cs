namespace CharacterPlanner.Simulation;

/// <summary>A versioned, runtime-independent SplitMix64 stream. Each fight has an independently mixed stream id.</summary>
public sealed class SimulationRandom
{
    public const string Contract = "d2p-splitmix64-v1";
    private const ulong Increment = 0x9e3779b97f4a7c15;
    private ulong state;

    public SimulationRandom(ulong seed, ulong stream = 0) => state = Mix(unchecked(seed + Increment * (stream + 1)));

    public ulong NextUInt64()
    {
        state = unchecked(state + Increment);
        return Mix(state);
    }

    /// <summary>Exactly the upper 53 random bits scaled into [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9);
        value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111eb);
        return value ^ (value >> 31);
    }
}
