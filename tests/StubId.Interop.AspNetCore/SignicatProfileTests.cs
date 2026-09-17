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
        Assert.Contains(keys, key => key.GetProperty("alg").GetString() == "RS256");
        Assert.Contains(keys, key => key.GetProperty("alg").GetString() == "RSA-OAEP");
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
    public async Task A_path_the_broker_refuses_is_refused_here(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
