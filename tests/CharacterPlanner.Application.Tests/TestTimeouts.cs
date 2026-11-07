namespace CharacterPlanner.Application.Tests;

/// <summary>
/// Concurrency tests wait on gates. A regression (a deadlock, a lost publish) must fail the
/// test rather than hang the run, so every async test gets this timeout; it also cancels
/// TestContext's token, which releases blocking waits on it.
/// </summary>
internal static class TestTimeouts
{
    public const int Gate = 30_000;
}
