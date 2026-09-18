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
/// The rest read a built container and are held back by the trait, because Build() is not free.
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
        Assert.Equal("ghcr.io/benne/stubid:2026.09.4", StubIdBuilder.StubIdImage);
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

    /// <summary>
    /// A second broker's authority is right before the container has started.
    /// </summary>
    /// <remarks>
    /// <c>/op</c> is the first broker's own path segment, and a container built for another broker
    /// reported it anyway until the instance had started and corrected it. That window is exactly
    /// when a suite reads the property: a relying party is configured while the test is being wired
    /// up, and a client library configured with the wrong path fails as a discovery error with the
    /// broker's name nowhere in it.
    /// </remarks>
    [Trait("Category", "Container")]
    [Fact]
    public void A_second_brokers_authority_is_right_before_the_container_starts()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .WithProfile("signicat")
            .Build();

        Assert.Equal("http://stubid.example:8080/auth/open", container.Authority.ToString());
    }

    /// <summary>
    /// A broker published after this module is passed through, and has no authority until asked.
    /// </summary>
    /// <remarks>
    /// The package and the image are versioned apart, so a name this module does not know is a
    /// later image rather than a mistake, and refusing it here would stop a suite running an image
    /// newer than its module. What the module may not do is guess a root for it, because a guess
    /// handed to a client library is the failure this property exists to prevent.
    /// </remarks>
    [Trait("Category", "Container")]
    [Fact]
    public void A_broker_this_module_does_not_know_has_no_authority_until_the_instance_says()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .WithProfile("a-broker-from-a-later-image")
            .Build();

        var refusal = Assert.Throws<InvalidOperationException>(() => container.Authority);

        Assert.Contains("a-broker-from-a-later-image", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("StartAsync", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And what the instance reports is what the authority uses, where nothing could compose one.
    /// </summary>
    /// <remarks>
    /// The other half of the refusal above, and the only path where the composed root cannot
    /// answer at all: an image newer than this package serves a broker whose root is written down
    /// nowhere here, says where it sits during start, and the authority is that. Driven through
    /// the seam the startup callback uses, because the image it would otherwise take is one that
    /// does not exist yet.
    /// </remarks>
    [Trait("Category", "Container")]
    [Fact]
    public void A_broker_the_module_cannot_place_takes_the_root_the_instance_reports()
    {
        var container = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .WithProfile("a-broker-from-a-later-image")
            .Build();

        container.AcceptRootFromInstance("later/root");

        Assert.Equal("http://stubid.example:8080/later/root", container.Authority.ToString());
    }

    /// <summary>An instance too old to name a root is taken to be serving the first broker.</summary>
    /// <remarks>
    /// Which is the only broker it can be serving: the field arrived before the second one did.
    /// What makes this worth a test is that the fallback used to be the module's own literal and
    /// is now the caller's word - so an instance that cannot contradict it is exactly where a
    /// wrong authority would have gone out unchallenged.
    /// </remarks>
    [Trait("Category", "Container")]
    [Fact]
    public void An_instance_that_names_no_root_is_taken_to_serve_the_first_broker()
    {
        var asked = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .WithProfile("signicat")
            .Build();

        Assert.Throws<InvalidOperationException>(() => asked.AcceptRootFromInstance(null));

        var served = new StubIdBuilder("stubid:none")
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .Build();

        served.AcceptRootFromInstance(null);

        Assert.Equal("http://stubid.example:8080/op", served.Authority.ToString());
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
