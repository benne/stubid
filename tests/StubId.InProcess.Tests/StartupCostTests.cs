using System.Diagnostics;

namespace StubId.InProcess.Tests;

/// <summary>What starting an instance costs, once the machine has keys.</summary>
/// <remarks>
/// The other half of the milestone's budget. The login test starts its stopwatch after the
/// instance is up, because the very first instance on a machine generates three signing keys and
/// that cost belongs to the machine rather than to this codebase - so the claim that starting is
/// cheap needs its own test, and that test has to warm the key directory itself rather than hope
/// something else did. Otherwise it passes on a developer's laptop and fails on a fresh runner.
/// </remarks>
public class StartupCostTests(ITestOutputHelper output)
{
    /// <summary>What starting is allowed to cost. Unchanged; it is the measurement that moved.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many starts this will pay for before giving up on finding one under budget.
    /// </summary>
    private const int Attempts = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starting is under budget, judged by the fastest of a few starts rather than by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One sample against a wall clock is a coin flip on shared hardware, and this test called
    /// it wrong twice in one milestone: 1.01 s once and 1.03 s later, both on a two-core runner
    /// with eight test assemblies going at once, and neither reproducible afterwards. Widening
    /// the ceiling would have bought the same quiet by throwing away the signal - a budget of
    /// two seconds passes a change that doubles the cost.
    /// </para>
    /// <para>
    /// The fastest start is the right statistic instead, because the noise here is strictly
    /// additive: another process taking the core can only ever make a start look slower, never
    /// faster. So the floor is the closest thing to what starting actually costs, and a change
    /// that makes starting more expensive raises the floor with everything else. What a single
    /// sample measures is that cost plus whatever the machine happened to be doing.
    /// </para>
    /// <para>
    /// It stops at the first start under budget, so the healthy case still pays for one. The
    /// remaining four are only bought by a machine that was busy, which is exactly when they
    /// are worth buying.
    /// </para>
    /// <para>
    /// Not all of the spread is the machine, either. Forced to take all five on an idle laptop,
    /// this measured 116, 56, 40, 43 and 47 ms - the first start paying for JIT that every later
    /// one finds already done. That part is systematic rather than random, and it lands entirely
    /// on the sample a single-shot test would have used.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_instance_starts_against_a_warm_key_directory_in_under_a_second()
    {
        // The keys, once, off the clock. Every start below finds them already on disk.
        await using (var warming = new StubIdHostBuilder().Build())
        {
            await warming.StartAsync(Ct);
        }

        List<TimeSpan> samples = [];

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var elapsed = Stopwatch.StartNew();

            await using var stub = new StubIdHostBuilder().Build();
            await stub.StartAsync(Ct);

            var ready = await stub.Control.IsReadyAsync(Ct);

            elapsed.Stop();

            Assert.True(ready, "The instance started but does not report itself ready.");

            samples.Add(elapsed.Elapsed);

            if (elapsed.Elapsed < Budget)
            {
                break;
            }
        }

        // Every sample, not just the verdict. A cost test that reports only that it failed
        // leaves nobody anything to work with - was it 1.02 s five times, or 4 s once?
        output.WriteLine(
            "Started and ready in "
            + string.Join(", ", samples.Select(s => $"{s.TotalMilliseconds:0} ms"))
            + $" over {samples.Count} start(s).");

        Assert.True(
            samples.Min() < Budget,
            $"The fastest of {samples.Count} starts took {samples.Min().TotalSeconds:0.00} s "
            + "against a warm key directory, which is over budget. Every start: "
            + string.Join(", ", samples.Select(s => $"{s.TotalSeconds:0.00} s")));
    }
}
