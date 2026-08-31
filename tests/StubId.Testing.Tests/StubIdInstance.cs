using System.Diagnostics;

namespace StubId.Testing.Tests;

/// <summary>One instance for the whole project, reset between tests.</summary>
/// <remarks>
/// A collection rather than an assembly fixture, and the difference is load-bearing: xUnit runs
/// collections in parallel and the tests inside one in sequence, while resetting an instance clears
/// the sessions for everybody. Two classes resetting each other's state in parallel would be a
/// failure that reproduces on one machine in five.
/// </remarks>
public sealed class StubIdInstance : IAsyncLifetime
{
    public StubIdContainer Container { get; private set; } = null!;

    /// <summary>
    /// How long the container took to become usable. Recorded and reported, never asserted on: it
    /// includes an image build on a cold machine, which is not a property of this codebase.
    /// </summary>
    public TimeSpan StartupDuration { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await StubIdImage.ResolveAsync(ct);

        var started = Stopwatch.StartNew();

        // Manual, so a login parks unless a test says otherwise. Automatic approval would decide
        // every login at the last tier and leave the timeout test nothing to time out; a test that
        // wants a completed login queues the outcome, which takes precedence anyway.
        Container = new StubIdBuilder(image)
            .WithControllableClock()
            .WithAutomaticApproval(false)
            .Build();

        await Container.StartAsync(ct);

        StartupDuration = started.Elapsed;
    }

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class StubIdCollection : ICollectionFixture<StubIdInstance>
{
    public const string Name = "StubID container";
}
