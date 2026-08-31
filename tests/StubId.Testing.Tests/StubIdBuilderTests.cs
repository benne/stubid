namespace StubId.Testing.Tests;

/// <summary>
/// What the builder decides before Docker is involved.
/// </summary>
/// <remarks>
/// Deliberately not container tests. These run on every platform CI builds on, including the one
/// with no Linux Docker daemon, which is also what keeps a trait filter for the container tests from
/// matching nothing in this assembly - VSTest aborts the run when a filter selects no test in an
/// assembly, so a suite that was entirely container tests would fail the cross-platform job by being
/// skipped correctly.
/// </remarks>
public class StubIdBuilderTests
{
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
    /// A Uri renders a bare authority with a trailing slash, and the issuer is this value with /op
    /// appended - so leaving it on would emit an issuer carrying two.
    /// </remarks>
    [Fact]
    public void A_trailing_slash_is_removed_before_the_issuer_is_built_from_it()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080/"))
            .Build();

        Assert.Equal("http://stubid.example:8080/op", container.Authority.ToString());
    }

    [Fact]
    public void The_published_image_and_port_are_what_the_module_declares()
    {
        Assert.Equal("ghcr.io/benne/stubid:2026.08.1", StubIdBuilder.StubIdImage);
        Assert.Equal(8080, StubIdBuilder.StubIdPort);
    }

    [Fact]
    public void An_address_is_required_when_one_is_pinned()
    {
        Assert.Throws<ArgumentNullException>(
            () => new StubIdBuilder("stubid:none").WithPublicBaseUrl(null!));
    }
}
