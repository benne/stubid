namespace StubId.Testing.Tests;

/// <summary>
/// What the builder decides, and what the container it builds reports about the address.
/// </summary>
/// <remarks>
/// Split by what each fact costs. The two that only ask the builder a question run on every platform
/// CI builds on, which is also what keeps a trait filter for the container tests from matching
/// nothing in this assembly - VSTest aborts the run when a filter selects no test in an assembly, so
/// a suite that was entirely container tests would fail the cross-platform job by being skipped
/// correctly.
/// <para>
/// The two that read a built container are held back by the trait, because Build() is not free.
/// Testcontainers validates the Docker endpoint there and throws DockerUnavailableException when no
/// provider answers a ping, and Build() is the only door to Authority and BaseAddress - the
/// container cannot be constructed around a configuration the builder did not finish. Nothing is
/// started, no image is pulled and no container runs, but the daemon still has to be reachable, and
/// the daemon on a Windows runner is reachable most of the time rather than all of it.
/// </para>
/// </remarks>
public class StubIdBuilderTests
{
    [Fact]
    public void The_published_image_and_port_are_what_the_module_declares()
    {
        Assert.Equal("ghcr.io/benne/stubid:2026.09.1", StubIdBuilder.StubIdImage);
        Assert.Equal(8080, StubIdBuilder.StubIdPort);
    }

    [Fact]
    public void An_address_is_required_when_one_is_pinned()
    {
        Assert.Throws<ArgumentNullException>(
            () => new StubIdBuilder("stubid:none").WithPublicBaseUrl(null!));
    }

    [Trait("Category", "Container")]
    [Fact]
    public void The_address_a_caller_pins_is_the_address_the_instance_reports()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .Build();

        // Authority is the string that has to be exact: it is what a client library is configured
        // with, and what the issuer is compared against. A bare Uri renders its own trailing slash,
        // which is why the authority is built by appending a segment rather than concatenating text.
        Assert.Equal("http://stubid.example:8080/op", container.Authority.ToString());
        Assert.Equal("stubid.example", container.BaseAddress.Host);
        Assert.Equal(8080, container.BaseAddress.Port);
    }

    /// <remarks>
    /// A Uri renders a bare authority with a trailing slash, so a caller copying one out of their
    /// own client configuration hands one over. What the module reports is unaffected, because the
    /// authority resolves "op" against the base rather than concatenating text; the trim in
    /// WithPublicBaseUrl is about the other half - the string the container is told, and so the
    /// issuer the server builds from it, which A_pinned_address_survives_the_handshake proves
    /// against a running one.
    /// </remarks>
    [Trait("Category", "Container")]
    [Fact]
    public void A_trailing_slash_does_not_reach_the_issuer()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080/"))
            .Build();

        Assert.Equal("http://stubid.example:8080/op", container.Authority.ToString());
    }
}
