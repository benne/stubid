using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// An instance configured for the second broker, through the composition it actually runs.
/// </summary>
/// <remarks>
/// Everything here is either served from a recording or a refusal that names its reason, because
/// that is all this profile claims. What the tests are for is the join: a root of two segments, an
/// issuer composed per request, a document derived at build time, and a route table whose
/// unemulated half has to be exactly the half that is unemulated.
/// </remarks>
public class SignicatProfileTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Address = "http://localhost";

    private readonly HttpClient _client;

    public SignicatProfileTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("StubId:Profile", "signicat");
            builder.UseSetting("StubId:PublicBaseUrl", Address);
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The two addresses this profile answers rather than refuses.</summary>
    private static readonly string[] Served =
    [
        "/auth/open/.well-known/openid-configuration",
        "/auth/open/.well-known/openid-configuration/jwks",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    /// <summary>
    /// The clients it publishes are its own, and it has none.
    /// </summary>
    /// <remarks>
    /// The roster in the engine is the first broker's three, and an instance serving this broker
    /// handed them out at <c>/_stubid/v1/clients</c> and listed them on the admin page - ids every
    /// route here refuses. An empty list is the answer until a login of this broker is recorded,
    /// and the first broker's instance still publishes three, which ControlClientTests holds.
    /// </remarks>
    [Fact]
    public async Task It_publishes_none_of_the_first_brokers_clients()
    {
        var clients = (await JsonAsync("/_stubid/v1/clients")).GetProperty("clients");

        Assert.Empty(clients.EnumerateArray());
    }

    /// <summary>And the page a person reads does not list them either.</summary>
    /// <remarks>
    /// The emulated page rather than the landing one: it is where the roster is rendered, so it is
    /// the only page where listing another broker's clients is possible in the first place.
    /// </remarks>
    [Fact]
    public async Task The_admin_page_lists_no_clients_for_this_broker()
    {
        using var page = await _client.GetAsync("/_stubid/admin/emulated", Ct);

        // Asserted, so that a page which stopped answering could not pass the absence below.
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("0a775a87-878c-4b83-abe3-ee29c720c3e7", html, StringComparison.Ordinal);
        Assert.Contains("have not been recorded", html, StringComparison.Ordinal);
    }

    /// <summary>What is served is the recording with the tenant's host swapped for this address.</summary>
    [Fact]
    public async Task Discovery_is_the_recording_with_the_host_swapped()
    {
        var recorded = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "fixtures", "signicat", "sandbox", "CAP-001", "response.raw"), Ct);

        using var response = await _client.GetAsync("/auth/open/.well-known/openid-configuration", Ct);
        var served = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=UTF-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            recorded,
            served.Replace(Address, "https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com", StringComparison.Ordinal));
    }

    /// <summary>
    /// Neither document carries a cache header, which is the recording rather than a default.
    /// </summary>
    /// <remarks>
    /// The first broker sends <c>public, max-age=770</c> on both and StubID answers <c>no-store</c>,
    /// a divergence of its own. Here the recording says nothing and so does the answer, which only
    /// stays true while these two handlers keep their own result rather than borrowing that one.
    /// </remarks>
    [Theory]
    [InlineData("/auth/open/.well-known/openid-configuration")]
    [InlineData("/auth/open/.well-known/openid-configuration/jwks")]
    public async Task What_is_served_carries_no_cache_header(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Empty(response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task The_issuer_is_this_address_and_the_tenant_root()
    {
        var discovery = await JsonAsync("/auth/open/.well-known/openid-configuration");

        Assert.Equal($"{Address}/auth/open", discovery.GetProperty("issuer").GetString());
    }

    /// <summary>Every published key has this broker's members, in its order, and no chain.</summary>
    [Fact]
    public async Task Every_published_key_has_the_recorded_shape()
    {
        var keys = (await JsonAsync("/auth/open/.well-known/openid-configuration/jwks"))
            .GetProperty("keys").EnumerateArray().ToList();

        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(
            ["kty", "use", "kid", "e", "n", "alg"], key.EnumerateObject().Select(m => m.Name)));
        Assert.All(keys, key => Assert.Equal("RSA", key.GetProperty("kty").GetString()));

        // Each key by what it is for, rather than by the set carrying both values somewhere: the
        // algorithm and the key id are what a client reads, and swapping them between the two keys
        // would leave every assertion above standing.
        var signing = Assert.Single(keys, key => key.GetProperty("use").GetString() == "sig");
        var encryption = Assert.Single(keys, key => key.GetProperty("use").GetString() == "enc");

        Assert.Equal("RS256", signing.GetProperty("alg").GetString());
        Assert.Equal("RSA-OAEP", encryption.GetProperty("alg").GetString());
        Assert.Matches("^signing-key-[0-9a-f]{32}$", signing.GetProperty("kid").GetString());
        Assert.Matches("^encryption-key-[0-9a-f]{32}$", encryption.GetProperty("kid").GetString());
    }

    /// <summary>The same key set on every request, which is the divergence this profile carries.</summary>
    [Fact]
    public async Task The_key_set_is_the_same_on_every_request()
    {
        var first = await JsonAsync("/auth/open/.well-known/openid-configuration/jwks");
        var second = await JsonAsync("/auth/open/.well-known/openid-configuration/jwks");

        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    /// <summary>
    /// Every advertised address is served or answers 501 with a reason that resolves.
    /// </summary>
    /// <remarks>
    /// Read from the document this instance serves rather than from a list beside it, so an
    /// endpoint that stopped being declared fails here rather than 404ing quietly for whoever
    /// discovered it.
    /// </remarks>
    [Fact]
    public async Task Every_advertised_address_is_served_or_says_why_it_is_not()
    {
        var discovery = await JsonAsync("/auth/open/.well-known/openid-configuration");

        var advertised = discovery.EnumerateObject()
            .Where(m => m.Name != "issuer")
            .Where(m => m.Value.ValueKind == JsonValueKind.String)
            .Where(m => m.Value.GetString()!.StartsWith(Address, StringComparison.Ordinal))
            .Select(m => m.Value.GetString()![Address.Length..])
            .ToList();

        Assert.NotEmpty(advertised);

        foreach (var path in advertised)
        {
            using var response = await _client.GetAsync(path, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                // Named, so that an endpoint that started answering 200 with something invented
                // reads as the failure it would be rather than as one more address that is served.
                Assert.Contains(path, Served, StringComparer.Ordinal);

                continue;
            }

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

            using var refusal = JsonDocument.Parse(body);
            var reason = refusal.RootElement.GetProperty("reason").GetString()!;

            Assert.Equal("not_implemented", refusal.RootElement.GetProperty("error").GetString());
            Assert.Contains(path, refusal.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);

            var (document, anchor) = Section(reason);

            Assert.True(File.Exists(document), $"{path} names {reason}, and that file is not there.");
            Assert.Contains($"<a id=\"{anchor}\"", await File.ReadAllTextAsync(document, Ct), StringComparison.Ordinal);
        }
    }

    /// <summary>The route table calls unemulated exactly what is unemulated.</summary>
    [Fact]
    public async Task The_route_table_marks_what_is_not_emulated()
    {
        var routes = (await JsonAsync("/_stubid/v1/routes")).GetProperty("routes").EnumerateArray();

        var unemulated = routes
            .Where(route => !route.GetProperty("emulated").GetBoolean())
            .Select(route => route.GetProperty("pattern").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
        [
            "/auth/open/connect/authorize",
            "/auth/open/connect/checksession",
            "/auth/open/connect/ciba",
            "/auth/open/connect/ciba/cancel",
            "/auth/open/connect/deviceauthorization",
            "/auth/open/connect/endsession",
            "/auth/open/connect/introspect",
            "/auth/open/connect/par",
            "/auth/open/connect/revocation",
            "/auth/open/connect/token",
            "/auth/open/connect/userinfo",
        ], unemulated);
    }

    [Fact]
    public async Task The_instance_says_which_broker_it_serves()
    {
        var fidelity = await JsonAsync("/_stubid/v1/fidelity");
        var profile = fidelity.GetProperty("profile");

        Assert.Equal("signicat", profile.GetProperty("broker").GetString());
        Assert.Equal("auth/open", profile.GetProperty("root").GetString());
        Assert.NotEmpty(fidelity.GetProperty("entries").EnumerateArray());
    }

    /// <summary>An instance answers with its own broker's ledger and nobody else's.</summary>
    [Fact]
    public async Task The_ledger_it_answers_with_is_its_own()
    {
        var entries = (await JsonAsync("/_stubid/v1/fidelity")).GetProperty("entries").EnumerateArray();

        var elsewhere = entries
            .Where(entry => entry.GetProperty("reason").ValueKind == JsonValueKind.String)
            .Where(entry => entry.GetProperty("reason").GetString()!.Contains("brokers/neb/", StringComparison.Ordinal))
            .Select(entry => entry.GetProperty("subject").GetString()!)
            .ToList();

        Assert.True(elsewhere.Count == 0,
            "These argue their case on the first broker's page: " + string.Join(", ", elsewhere));
    }

    /// <summary>The first broker's surface is not served beside this one.</summary>
    [Fact]
    public async Task The_other_brokers_surface_is_not_served()
    {
        using var response = await _client.GetAsync("/op/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>What the recordings settle about paths this broker refuses.</summary>
    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/openid-configuration/auth/open")]
    [InlineData("/.well-known/oauth-authorization-server/auth/open")]
    [InlineData("/Auth/open/.well-known/openid-configuration")]
    [InlineData("/auth/Open/.well-known/openid-configuration")]
    [InlineData("/auth/open/.well-known/openid-configuration/")]
    [InlineData("/auth/open/.well-known/openid-configuration/jwks/")]
    [InlineData("/auth/open/connect/userinfo/")]
    public async Task A_path_the_broker_refuses_is_refused_here(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Case below the root does not decide whether a path is matched, on either family.
    /// </summary>
    /// <remarks>
    /// Both tenant segments above it are compared exactly, and the recordings say the broker stops
    /// being strict below them: the key set answers to JWKS (CAP-044), and Userinfo reaches
    /// userinfo rather than a 404 (CAP-045). What is checked here is that the path was matched -
    /// the served document is served, and the endpoint that is not emulated answers for itself
    /// rather than reporting that nothing is there.
    /// </remarks>
    [Theory]
    [InlineData("/auth/open/.well-known/openid-configuration/JWKS", HttpStatusCode.OK)]
    [InlineData("/auth/open/connect/Userinfo", HttpStatusCode.NotImplemented)]
    public async Task Case_below_the_root_does_not_decide_whether_a_path_is_matched(
        string path, HttpStatusCode expected)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>A method the document is not served for is refused as one.</summary>
    /// <remarks>
    /// CAP-048: the broker answers 405, where a 404 would tell a client the document is not there
    /// at all. The route declares GET alone, so the framework answers the same status - this is
    /// what says the two agree, rather than a rule of StubID's own being enforced.
    /// </remarks>
    [Fact]
    public async Task Discovery_refuses_a_method_it_does_not_serve()
    {
        using var content = new StringContent("");
        using var response = await _client.PostAsync(
            "/auth/open/.well-known/openid-configuration", content, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>Every answer says it came from an emulator.</summary>
    [Theory]
    [InlineData("/auth/open/.well-known/openid-configuration")]
    [InlineData("/auth/open/connect/authorize")]
    [InlineData("/auth/open/nothing-here")]
    public async Task Every_kind_of_answer_carries_the_emulator_header(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-StubID-Emulator")));
    }

    private static (string Document, string Anchor) Section(string reason)
    {
        var path = reason.Replace("https://github.com/benne/stubid/blob/master/", "", StringComparison.Ordinal);
        var parts = path.Split('#');

        return (Path.Combine(RepositoryRoot(), parts[0].Replace('/', Path.DirectorySeparatorChar)), parts[1]);
    }
}
