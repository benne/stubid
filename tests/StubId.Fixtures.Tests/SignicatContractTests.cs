using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the second broker's unattended recordings oblige, read from the recordings themselves.
/// </summary>
/// <remarks>
/// The same job the first broker's contract tests do, and for the same reason: a recording nobody
/// asserts against is a file, not a contract. These run without a server, so they say what the
/// pack settles rather than what StubID does with it - the instance is checked against them
/// separately.
/// </remarks>
public class SignicatContractTests
{
    private static JsonElement Document(string captureId) =>
        JsonDocument.Parse(File.ReadAllText(
            Repository.Fixture(BrokerTarget.Signicat, captureId, "response.raw"))).RootElement;

    /// <summary>
    /// Every address in the document is on the tenant's own host.
    /// </summary>
    /// <remarks>
    /// Which is what makes one substitution enough to serve it: the build swaps that host for this
    /// instance's address, and a URL pointing anywhere else would survive into the served document
    /// pointing at the broker.
    /// </remarks>
    [Fact]
    public void Every_address_discovery_gives_is_on_the_tenant_host()
    {
        var elsewhere = Document("CAP-001").EnumerateObject()
            .Where(m => m.Value.ValueKind == JsonValueKind.String)
            .Where(m => m.Value.GetString()!.StartsWith("https://", StringComparison.Ordinal))
            .Where(m => !m.Value.GetString()!.StartsWith(
                "https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com", StringComparison.Ordinal))
            .Select(m => $"{m.Name} -> {m.Value.GetString()}")
            .ToList();

        Assert.True(elsewhere.Count == 0,
            "These name a host the substitution would not replace: " + string.Join(", ", elsewhere));
    }

    /// <summary>The issuer is the tenant root, so the served issuer ends with the profile's own root.</summary>
    [Fact]
    public void The_issuer_is_the_tenant_root()
    {
        Assert.Equal(
            "https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com/auth/open",
            Document("CAP-001").GetProperty("issuer").GetString());
    }

    /// <summary>
    /// The addresses it advertises, in the order it advertises them.
    /// </summary>
    /// <remarks>
    /// Written out rather than counted, because this is the list the profile has to answer or
    /// declare missing, and <c>check_session_iframe</c> is the reason: it is an address the broker
    /// serves whose member name ends in neither <c>_endpoint</c> nor <c>_uri</c>.
    /// </remarks>
    [Fact]
    public void These_are_the_addresses_it_advertises()
    {
        var issuer = Document("CAP-001").GetProperty("issuer").GetString()!;
        var host = issuer[..^"/auth/open".Length];

        var advertised = Document("CAP-001").EnumerateObject()
            .Where(m => m.Name != "issuer")
            .Where(m => m.Value.ValueKind == JsonValueKind.String)
            .Where(m => m.Value.GetString()!.StartsWith(host, StringComparison.Ordinal))
            .Select(m => m.Value.GetString()![host.Length..])
            .ToList();

        Assert.Equal(
        [
            "/auth/open/.well-known/openid-configuration/jwks",
            "/auth/open/connect/authorize",
            "/auth/open/connect/token",
            "/auth/open/connect/userinfo",
            "/auth/open/connect/endsession",
            "/auth/open/connect/checksession",
            "/auth/open/connect/revocation",
            "/auth/open/connect/introspect",
            "/auth/open/connect/deviceauthorization",
            "/auth/open/connect/ciba",
            "/auth/open/connect/par",
            "/auth/open/connect/ciba/cancel",
        ], advertised);
    }

    /// <summary>
    /// Every key carries six members in one order, and no certificate.
    /// </summary>
    /// <remarks>
    /// The first broker publishes certificates: <c>x5t</c>, an <c>x5c</c> chain, and no
    /// <c>alg</c>. Every one of those is the other way round here, which is why the two key sets
    /// are written by different code rather than one helper with arguments.
    /// </remarks>
    [Fact]
    public void Every_key_carries_six_members_in_order_and_no_certificate()
    {
        var keys = Document("CAP-002").GetProperty("keys").EnumerateArray().ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal(
            ["kty", "use", "kid", "e", "n", "alg"],
            key.EnumerateObject().Select(m => m.Name)));
    }

    [Fact]
    public void A_signing_key_is_RS256_and_an_encryption_key_is_RSA_OAEP()
    {
        var keys = Document("CAP-002").GetProperty("keys").EnumerateArray().ToList();

        Assert.All(keys, key => Assert.Equal("RSA", key.GetProperty("kty").GetString()));
        Assert.All(keys, key => Assert.Equal(
            key.GetProperty("use").GetString() == "sig" ? "RS256" : "RSA-OAEP",
            key.GetProperty("alg").GetString()));

        Assert.Contains(keys, key => key.GetProperty("use").GetString() == "sig");
        Assert.Contains(keys, key => key.GetProperty("use").GetString() == "enc");
    }

    /// <summary>
    /// A refusal below the root is the application's, and one outside it is the edge's.
    /// </summary>
    /// <remarks>
    /// Both are 404, and the difference is the body: the application answers an empty one, while
    /// anything that never reaches it gets the host's HTML page. StubID answers both empty, which
    /// is a divergence its own page carries; what this pins is the recording that says so.
    /// </remarks>
    [Theory]
    [InlineData("CAP-003")]
    [InlineData("CAP-004")]
    [InlineData("CAP-005")]
    [InlineData("CAP-007")]
    [InlineData("CAP-008")]
    public void A_path_outside_the_root_earns_the_hosts_page(string captureId)
    {
        var head = File.ReadAllText(Repository.Fixture(BrokerTarget.Signicat, captureId, "response.head"));
        var body = File.ReadAllText(Repository.Fixture(BrokerTarget.Signicat, captureId, "response.raw"));

        Assert.StartsWith("HTTP 404", head, StringComparison.Ordinal);
        Assert.Contains("text/html", head, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void A_trailing_slash_below_the_root_earns_an_empty_404()
    {
        var head = File.ReadAllText(Repository.Fixture(BrokerTarget.Signicat, "CAP-006", "response.head"));
        var body = File.ReadAllText(Repository.Fixture(BrokerTarget.Signicat, "CAP-006", "response.raw"));

        Assert.StartsWith("HTTP 404", head, StringComparison.Ordinal);
        Assert.Empty(body);

        // By line, because X-Content-Type-Options carries the words and says nothing about a body.
        Assert.DoesNotContain(
            head.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
    }
}
