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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_instance_starts_against_a_warm_key_directory_in_under_a_second()
    {
        await using (var warming = new StubIdHostBuilder().Build())
        {
            await warming.StartAsync(Ct);
        }

        var elapsed = Stopwatch.StartNew();

        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        var ready = await stub.Control.IsReadyAsync(Ct);

        elapsed.Stop();

        Assert.True(ready, "The instance started but does not report itself ready.");

        output.WriteLine($"Started and ready in {elapsed.Elapsed.TotalMilliseconds:0} ms.");

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"Starting took {elapsed.Elapsed.TotalSeconds:0.00} s against a warm key directory.");
    }
}
