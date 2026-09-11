using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The address book a run records against.
/// </summary>
/// <remarks>
/// The packs are the part worth pinning. Before this record existed the sitting's directory was
/// found by walking three parent directories from the unattended one and re-appending a literal
/// path, by depth alone and without checking a single segment name — so any pack at the same
/// depth was written into the first broker's session directory, silently.
/// </remarks>
public class BrokerTargetTests
{
    [Theory]
    [InlineData("neb", "fixtures/neb/pp", "fixtures/neb/pp-session")]
    [InlineData("signicat", "fixtures/signicat/sandbox", "fixtures/signicat/sandbox-session")]
    public void A_broker_names_its_own_two_packs(string key, string pack, string session)
    {
        var target = BrokerTarget.Select(key);

        Assert.Equal(pack, target.Pack.Replace('\\', '/'));
        Assert.Equal(session, target.SessionPack.Replace('\\', '/'));
    }

    [Fact]
    public void No_two_brokers_share_a_pack()
    {
        Assert.Equal(
            BrokerTarget.All.Count * 2,
            BrokerTarget.All
                .SelectMany(t => new[] { t.Pack, t.SessionPack })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    /// <summary>
    /// There is no default, and the refusal says why rather than only that.
    /// </summary>
    /// <remarks>
    /// A default would mean a run that forgot the argument wrote one broker's recordings where
    /// the other's belong, and the two packs look alike enough that nobody would notice until a
    /// manifest disagreed.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nets")]
    [InlineData("signaturgruppen")]
    public void A_run_that_names_no_broker_this_harness_records_is_refused(string? key)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => BrokerTarget.Select(key));

        // The vocabulary is in the message, because a refusal that does not name the options
        // makes the reader guess them.
        Assert.Contains("neb", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("signicat", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("neb")]
    [InlineData("NEB")]
    [InlineData("  signicat  ")]
    public void A_name_is_matched_the_way_somebody_types_it(string key)
    {
        Assert.NotNull(BrokerTarget.Select(key));
    }

    /// <summary>
    /// The second broker's authority is a template, and it stays one until it is resolved.
    /// </summary>
    /// <remarks>
    /// The tenant is the hostname there, so the account's own subdomain is in every absolute URL
    /// a recording carries. Keeping the template unresolved in the catalog is what makes the
    /// scrubber's round trip write the placeholder back into the fixture.
    /// </remarks>
    [Fact]
    public void The_tenant_is_a_placeholder_until_the_settings_hold_it()
    {
        var target = BrokerTarget.Select("signicat");

        Assert.Contains("{{SIGNICAT_DOMAIN}}", target.AuthorityTemplate, StringComparison.Ordinal);

        // Which is also a scrubber placeholder, so a recording made without the setting refuses
        // rather than writing somebody's account name into a fixture.
        Assert.Contains(
            Scrubber.Credentials,
            entry => target.AuthorityTemplate.Contains(entry.Placeholder, StringComparison.Ordinal));
    }

    [Fact]
    public void The_first_brokers_authority_needs_nothing_configured()
    {
        Assert.True(BrokerTarget.Select("neb").TryResolveAuthority(out var authority));
        Assert.Equal("https://pp.netseidbroker.dk/op", authority);
    }
}
