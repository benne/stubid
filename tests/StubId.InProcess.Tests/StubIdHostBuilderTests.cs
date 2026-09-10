namespace StubId.InProcess.Tests;

/// <summary>What the builder decides, before anything is started.</summary>
/// <remarks>
/// These start no host and touch nothing, which is the point twice over: the builder is meant to
/// capture settings rather than do work, and an assembly whose every test needs a running instance
/// is slower to trust than one that can answer most questions without one.
/// </remarks>
public class StubIdHostBuilderTests
{
    [Fact]
    public void The_address_a_caller_pins_is_the_address_the_instance_reports()
    {
        var stub = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid.example:8443"))
            .Build();

        Assert.Equal("https://stubid.example:8443", stub.BaseAddress.ToString().TrimEnd('/'));
        Assert.Equal("https://stubid.example:8443/op", stub.Authority.ToString());
    }

    /// <remarks>
    /// The issuer is compared character for character by the client libraries that matter, so a
    /// trailing slash the caller happened to type must not reach one.
    /// </remarks>
    [Fact]
    public void A_trailing_slash_is_removed_before_the_issuer_is_built_from_it()
    {
        var stub = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid.example/"))
            .Build();

        Assert.Equal("https://stubid.example/op", stub.Authority.ToString());
    }

    /// <remarks>
    /// Both halves matter. https is what lets a client library keep its metadata check on, and a
    /// name reserved by RFC 2606 is what makes a forgotten back-channel handler fail by naming the
    /// host it could not find rather than by reaching whatever else answers on that machine.
    /// </remarks>
    /// <summary>
    /// The authority follows the broker, and the broker is chosen rather than assumed.
    /// </summary>
    /// <remarks>
    /// <c>/op</c> is Nets eID Broker's own path segment, not StubID's, and it used to be a literal
    /// here. A caller pointing a client library at this property is pointing it at whichever
    /// broker the instance was built to serve, and the two answers have to come from the same
    /// place or a discovery fails with the broker's name nowhere in the error.
    /// </remarks>
    [Fact]
    public void The_authority_carries_the_path_the_chosen_broker_serves_under()
    {
        var chosen = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid.example"))
            .WithProfile("neb")
            .Build();

        var implied = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid.example"))
            .Build();

        Assert.Equal("https://stubid.example/op", chosen.Authority.ToString());
        Assert.Equal(chosen.Authority, implied.Authority);
    }

    /// <summary>A broker this build does not serve is refused where the caller can still see it.</summary>
    /// <remarks>
    /// At <c>Build</c> rather than at start, because the authority is computed from the same
    /// answer and a caller reads that before it starts anything. A name that was quietly ignored
    /// would start the wrong broker and be found by a client library failing to discover a
    /// document, several layers from the line that caused it.
    /// </remarks>
    [Fact]
    public void A_broker_this_build_does_not_serve_is_refused_at_build()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => new StubIdHostBuilder().WithProfile("signaturgruppen").Build());

        Assert.Contains("signaturgruppen", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("neb", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpinned_instance_calls_itself_a_name_that_cannot_resolve()
    {
        var stub = new StubIdHostBuilder().Build();

        Assert.StartsWith("https://", stub.BaseAddress.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(".invalid", stub.BaseAddress.Host, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The placeholder the recorded discovery document carries is <c>stubid.invalid</c>. Serving
    /// that as the address would substitute the host for itself, and a document that had stopped
    /// being rewritten would still look right - which is the class of quiet wrongness this whole
    /// project exists to catch.
    /// </remarks>
    [Fact]
    public void The_default_address_is_not_the_placeholder_it_replaces()
    {
        Assert.NotEqual("https://stubid.invalid", StubIdHostBuilder.DefaultPublicBaseUrl);
    }

    [Fact]
    public void An_address_is_required_when_one_is_pinned()
    {
        Assert.Throws<ArgumentNullException>(() => new StubIdHostBuilder().WithPublicBaseUrl(null!));
    }

    /// <remarks>
    /// There is no WithTls to refuse, so the refusal has to live where the setting could still
    /// arrive. Accepting it would have the instance write a certificate nothing serves and report
    /// over the control API that it serves TLS.
    /// </remarks>
    [Fact]
    public void Asking_for_TLS_in_process_says_where_TLS_lives()
    {
        var refusal = Assert.Throws<ArgumentException>(
            () => new StubIdHostBuilder().WithSetting("StubId:Tls", "self-signed"));

        Assert.Contains("StubId.Testing", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_the_typed_surface_does_not_cover_is_carried()
    {
        var stub = new StubIdHostBuilder()
            .WithSetting("StubId:PublicBaseUrl", "https://carried.example")
            .Build();

        Assert.Equal("https://carried.example/op", stub.Authority.ToString());
    }
}
