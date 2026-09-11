using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The registrations a sitting records with.
/// </summary>
/// <remarks>
/// These replaced an enum, so the compiler stopped checking two things it used to. Both are
/// checked here instead, and one of them the enum never checked at all.
/// </remarks>
public class BrokerClientTests
{
    /// <summary>
    /// Every registration reads the settings the switch it replaced read.
    /// </summary>
    /// <remarks>
    /// Written out by hand on purpose. This is a rename check and the rename is the whole risk
    /// of moving seven enum arms into a table: a transposed setting name would compile, pass
    /// every other test, and record a sitting with the wrong client's credentials.
    /// </remarks>
    [Theory]
    [InlineData("private", null, "STUBID_NEB_PP_CLIENT_ID", "STUBID_NEB_PP_CLIENT_SECRET")]
    [InlineData("open code", "0a775a87-878c-4b83-abe3-ee29c720c3e7", null, "STUBID_NEB_PP_CODE_CLIENT_SECRET")]
    [InlineData("open implicit", "93ed8e0d-93ad-405c-b1ac-8bf13d484941", null, "STUBID_NEB_PP_CODE_CLIENT_SECRET")]
    [InlineData("restricted redirect", null, "STUBID_NEB_PP_SSO_A_CLIENT_ID", "STUBID_NEB_PP_SSO_A_CLIENT_SECRET")]
    [InlineData("single sign-on A", null, "STUBID_NEB_PP_SSO_A_CLIENT_ID", "STUBID_NEB_PP_SSO_A_CLIENT_SECRET")]
    [InlineData("single sign-on B", null, "STUBID_NEB_PP_SSO_B_CLIENT_ID", "STUBID_NEB_PP_SSO_B_CLIENT_SECRET")]
    [InlineData("hybrid", null, "STUBID_NEB_PP_SSO_C_CLIENT_ID", "STUBID_NEB_PP_SSO_C_CLIENT_SECRET")]
    public void A_registration_reads_what_it_always_read(
        string name, string? published, string? idSetting, string secretSetting)
    {
        var client = BrokerClient.NetsEidBroker.All.Single(c => c.Name == name);

        Assert.Equal(published, client.PublishedId);
        Assert.Equal(idSetting, client.IdSetting);
        Assert.Equal(secretSetting, client.SecretSetting);
    }

    /// <summary>
    /// A name identifies a registration within its broker, and only within it.
    /// </summary>
    /// <remarks>
    /// Both brokers have a client called "hybrid", because on both it is the hybrid grant and
    /// there is no better word. That is fine and it is also the reason nothing may look one up
    /// across the whole roster - which one test did, and which passed for exactly as long as the
    /// second broker had no clients.
    /// </remarks>
    [Fact]
    public void A_name_is_unique_within_a_broker_but_not_across_them()
    {
        foreach (var broker in Enum.GetValues<Broker>())
        {
            var names = BrokerClient.All.Where(c => c.Broker == broker).Select(c => c.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.Equal(2, BrokerClient.All.Count(c => c.Name == "hybrid"));
    }

    [Fact]
    public void The_roster_is_the_seven_the_enum_named()
    {
        Assert.Equal(7, BrokerClient.NetsEidBroker.All.Count);
    }

    /// <summary>
    /// The second broker's four, and the settings each reads.
    /// </summary>
    /// <remarks>
    /// Written out by hand for the same reason the first broker's are, with one difference that
    /// matters more here: these were registered by hand in a dashboard, so a transposed setting
    /// name would record a sitting under a client configured for something else and the recording
    /// would look perfectly ordinary. None of them is published, so every one needs both settings.
    /// </remarks>
    [Theory]
    [InlineData("primary", "STUBID_SIGNICAT_PRIMARY_CLIENT_ID", "STUBID_SIGNICAT_PRIMARY_CLIENT_SECRET")]
    [InlineData("claims", "STUBID_SIGNICAT_CLAIMS_CLIENT_ID", "STUBID_SIGNICAT_CLAIMS_CLIENT_SECRET")]
    [InlineData("hybrid", "STUBID_SIGNICAT_HYBRID_CLIENT_ID", "STUBID_SIGNICAT_HYBRID_CLIENT_SECRET")]
    [InlineData("partner", "STUBID_SIGNICAT_PARTNER_CLIENT_ID", "STUBID_SIGNICAT_PARTNER_CLIENT_SECRET")]
    public void A_signicat_registration_reads_its_own_pair(
        string name, string idSetting, string secretSetting)
    {
        var client = BrokerClient.Signicat.All.Single(c => c.Name == name);

        Assert.Equal(Broker.Signicat, client.Broker);
        Assert.Null(client.PublishedId);
        Assert.Equal(idSetting, client.IdSetting);
        Assert.Equal(secretSetting, client.SecretSetting);
    }

    /// <summary>
    /// No two registrations share a pair of settings on the second broker.
    /// </summary>
    /// <remarks>
    /// The first broker's roster shares deliberately - its restricted-redirect entry is single
    /// sign-on A under another name - so this cannot be asserted across both. Here a shared pair
    /// would be a copied line rather than a decision, and it would quietly record two of the four
    /// configurations as the same client.
    /// </remarks>
    [Fact]
    public void The_four_signicat_registrations_are_four_distinct_clients()
    {
        var clients = BrokerClient.Signicat.All;

        Assert.Equal(4, clients.Count);
        Assert.Equal(4, clients.Select(c => c.IdSetting).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, clients.Select(c => c.SecretSetting).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, clients.Select(c => c.Summary).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every step names a registration belonging to the broker whose sitting it is.
    /// </summary>
    /// <remarks>
    /// This is the check the enum made for free and a table does not, and it also catches the
    /// mistake the enum could not express at all: a step pointing at another broker's client.
    /// </remarks>
    [Fact]
    public void Every_step_names_a_client_of_its_own_broker()
    {
        var wrong = Enum.GetValues<Broker>()
            .SelectMany(broker => ManualCatalog.For(broker)
                .Where(c => c.Client.Broker != broker)
                .Select(c => $"{broker}: {c.Id} records with {c.Client.Broker}'s {c.Client.Name}"))
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// Every setting a registration names is one the scrubber replaces.
    /// </summary>
    /// <remarks>
    /// A client identifier comes back echoed in a login redirect, which is how a private one
    /// reached a fixture the first time round. A registration added without its settings being
    /// added to the scrubber would record that again, and nothing else would notice.
    /// </remarks>
    [Fact]
    public void Every_setting_a_client_names_is_one_the_scrubber_replaces()
    {
        var scrubbed = Scrubber.Credentials.Select(c => c.Setting).ToHashSet(StringComparer.Ordinal);

        var unscrubbed = BrokerClient.All
            .SelectMany(client => new[] { client.IdSetting, client.SecretSetting })
            .Where(setting => setting is not null && !scrubbed.Contains(setting))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(unscrubbed.Count == 0,
            "These are read to record with and never replaced on the way into a fixture: "
            + string.Join(", ", unscrubbed));
    }

    /// <summary>
    /// A registration whose setting does not resolve refuses, and names both halves.
    /// </summary>
    /// <remarks>
    /// The resolver is passed in rather than the environment cleared. Clearing a variable proves
    /// nothing here: the settings file answers underneath it, so this passed on a fresh checkout
    /// and failed on a machine set up to record - which is the trap the shared collection in this
    /// project exists because of.
    /// </remarks>
    [Fact]
    public void A_registration_whose_setting_is_missing_says_which_one_and_whose()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => BrokerClient.NetsEidBroker.SsoB.Secret(_ => null));

        Assert.Contains("STUBID_NEB_PP_SSO_B_CLIENT_SECRET", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("single sign-on B", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing setting still blocks exactly the steps it always blocked.
    /// </summary>
    /// <remarks>
    /// The expected lists are the deleted switch's answers, written out rather than derived, so
    /// this compares the roster against what the code used to do instead of against itself. The
    /// two single sign-on entries share a registration's settings, which is why one missing value
    /// blocks both the sign-on step and the redirect refusal.
    /// </remarks>
    [Theory]
    [InlineData("STUBID_NEB_PP_CLIENT_ID", "CAP-023,CAP-020,CAP-021,CAP-022,CAP-031,CAP-024,CAP-027")]
    [InlineData("STUBID_NEB_PP_CLIENT_SECRET", "CAP-023,CAP-020,CAP-021,CAP-022,CAP-031,CAP-024,CAP-027")]
    [InlineData("STUBID_NEB_PP_CODE_CLIENT_SECRET", "CAP-026")]
    [InlineData("STUBID_NEB_PP_SSO_A_CLIENT_ID", "CAP-028,CAP-025")]
    [InlineData("STUBID_NEB_PP_SSO_A_CLIENT_SECRET", "CAP-028,CAP-025")]
    [InlineData("STUBID_NEB_PP_SSO_B_CLIENT_SECRET", "CAP-029")]
    [InlineData("STUBID_NEB_PP_SSO_C_CLIENT_ID", "CAP-030")]
    [InlineData("STUBID_NEB_PP_SSO_D_CLIENT_ID", "")]
    public void A_missing_setting_blocks_the_steps_it_always_blocked(string setting, string blocked)
    {
        Assert.Equal(
            blocked.Split(',', StringSplitOptions.RemoveEmptyEntries),
            ManualCatalog.StepsNeeding(BrokerTarget.NetsEidBroker, setting));
    }

    /// <summary>A published identifier needs nothing configured at all.</summary>
    [Fact]
    public void A_published_client_resolves_with_no_settings()
    {
        Assert.Equal(
            "0a775a87-878c-4b83-abe3-ee29c720c3e7",
            BrokerClient.NetsEidBroker.OpenCode.ClientId(_ => null));
    }
}
