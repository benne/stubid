namespace StubId.InProcess.Tests;

/// <summary>What an instance will and will not answer before it is started.</summary>
public class HostLifetimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <remarks>
    /// The documented difference from the container module. Docker assigns a port, so a container
    /// cannot say where it answers until it is running; here the caller chose the address, which
    /// means a relying party can be configured against an instance that does not exist yet.
    /// </remarks>
    [Fact]
    public void The_authority_is_known_before_the_instance_is_started()
    {
        var stub = new StubIdHostBuilder().Build();

        Assert.Equal($"{StubIdHostBuilder.DefaultPublicBaseUrl}/op", stub.Authority.ToString());
    }

    /// <remarks>
    /// Refused rather than started implicitly. A property that quietly builds a host is a property
    /// that quietly generates keys and loads a profile, which is not something to do from a
    /// getter somebody wrote in a debugger watch window.
    /// </remarks>
    [Fact]
    public void Members_that_need_a_transport_refuse_before_the_instance_is_started()
    {
        var stub = new StubIdHostBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => stub.Control);
        Assert.Throws<InvalidOperationException>(() => stub.CreateHandler());
        Assert.Throws<InvalidOperationException>(() => stub.CreateClient());
        Assert.Throws<InvalidOperationException>(() => stub.Services);
    }

    [Fact]
    public async Task Starting_an_instance_twice_is_refused()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stub.StartAsync(Ct));
    }

    [Fact]
    public async Task Disposing_an_instance_twice_is_harmless()
    {
        var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        await stub.DisposeAsync();
        await stub.DisposeAsync();
    }
}
