using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Wire;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What the slice has to do before anything is built on top of it.
/// </summary>
public class SliceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    /// <summary>So a cancelled run stops promptly rather than finishing every request.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SliceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    [Fact]
    public async Task Discovery_matches_the_recording_with_the_host_swapped()
    {
        var recorded = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "fixtures", "neb", "pp", "CAP-001", "response.raw"), Ct);

        var served = await _client.GetStringAsync("/op/.well-known/openid-configuration", Ct);

        Assert.Equal(
            recorded.Replace("https://pp.netseidbroker.dk", "http://localhost", StringComparison.Ordinal),
            served);
    }

    [Theory]
    [InlineData("scopes_supported")]
    [InlineData("claims_supported")]
    [InlineData("acr_values_supported")]
    public async Task Discovery_leaves_out_what_the_broker_leaves_out(string member)
    {
        using var document = JsonDocument.Parse(
            await _client.GetStringAsync("/op/.well-known/openid-configuration", Ct));

        Assert.False(document.RootElement.TryGetProperty(member, out _));
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration/op")]
    [InlineData("/.well-known/oauth-authorization-server/op")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task The_alternate_metadata_layouts_are_not_served(string path)
    {
        // Serving these would let a misconfigured client pass here and fail against the
        // broker, which is the failure this project exists to prevent.
        var response = await _client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_key_set_has_the_recorded_shape()
    {
        using var document = JsonDocument.Parse(
            await _client.GetStringAsync("/op/.well-known/openid-configuration/jwks", Ct));

        var keys = document.RootElement.GetProperty("keys").EnumerateArray().ToList();

        Assert.Equal(3, keys.Count);
        Assert.Equal(2, keys.Count(k => k.GetProperty("use").GetString() == "sig"));
        Assert.All(keys, k => Assert.False(k.TryGetProperty("alg", out _)));
        Assert.All(keys, k => Assert.Matches("^[0-9A-F]{40}$", k.GetProperty("kid").GetString()));
    }

    [Fact]
    public async Task A_full_login_returns_tokens_a_client_can_use()
    {
        var verifier = Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=abc&nonce=n-0S6_WzA2Mj&code_challenge={challenge}&code_challenge_method=S256", Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var redirect = authorize.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, redirect, StringComparison.Ordinal);

        var returned = System.Web.HttpUtility.ParseQueryString(redirect.Split('?')[1]);
        Assert.Equal("abc", returned["state"]);
        Assert.Equal("http://localhost/op", returned["iss"]);

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", returned["code"]!),
            new("redirect_uri", RedirectUri),
            new("code_verifier", verifier),
            new("client_id", CodeClient),
            new("client_secret", "any-secret-the-existing-configuration-carries"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Bearer", body.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(10800, body.RootElement.GetProperty("expires_in").GetInt32());

        var idToken = body.RootElement.GetProperty("id_token").GetString()!;
        using var payload = JsonDocument.Parse(Base64Url.Decode(idToken.Split('.')[1]));

        Assert.Equal("http://localhost/op", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(CodeClient, payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal("n-0S6_WzA2Mj", payload.RootElement.GetProperty("nonce").GetString());
        Assert.Equal("private", payload.RootElement.GetProperty("identity_type").GetString());
        Assert.Equal(
            "https://data.gov.dk/concept/core/nsis/Substantial",
            payload.RootElement.GetProperty("loa").GetString());

        // Userinfo, and the typing that a client will trip over if it is wrong.
        var access = body.RootElement.GetProperty("access_token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var userinfo = await _client.SendAsync(request, Ct);

        using var claims = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync(Ct));
        Assert.Equal(JsonValueKind.String, claims.RootElement.GetProperty("mitid.age").ValueKind);
        Assert.Equal(JsonValueKind.String, claims.RootElement.GetProperty("mitid.has_cpr").ValueKind);
        Assert.Equal("true", claims.RootElement.GetProperty("mitid.has_cpr").GetString());
    }

    [Fact]
    public async Task An_authorization_code_cannot_be_used_twice()
    {
        var (code, _) = await Authorize();

        var first = await Redeem(code);
        var second = await Redeem(code);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("""{"error":"invalid_grant"}""", await second.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_unknown_client_is_not_redirected_back()
    {
        // The client is told nothing at all. Redirecting an error back would make an
        // integration look correct here and hang against the broker.
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id=00000000-0000-0000-0000-000000000000" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();

        Assert.Contains("/op/Error?errorId=", location, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5099", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_errors_say_nothing_beyond_the_code()
    {
        using var response = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", "not-a-real-code"),
            new("client_id", "00000000-0000-0000-0000-000000000000"),
            new("client_secret", "irrelevant"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_client"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Userinfo_without_a_token_challenges_exactly_as_recorded()
    {
        using var response = await _client.GetAsync("/op/connect/userinfo", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(
            "Bearer realm=\"IdentityServer\",error=\"invalid_token\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_pushed_request_is_redeemed_by_reference()
    {
        // Discovery advertises the endpoint, and .NET 9 and later push by default, so this is
        // the first protocol request a stock client makes.
        using var pushed = await _client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new("client_id", CodeClient),
            new("response_type", "code"),
            new("redirect_uri", RedirectUri),
            new("scope", "openid mitid"),
            new("state", "par-state"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        using var body = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));
        var requestUri = body.RootElement.GetProperty("request_uri").GetString()!;

        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", requestUri, StringComparison.Ordinal);
        Assert.Equal(600, body.RootElement.GetProperty("expires_in").GetInt32());

        var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&request_uri={Uri.EscapeDataString(requestUri)}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("code=", authorize.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("state=par-state", authorize.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_post_returns_a_self_submitting_form()
    {
        // ASP.NET Core asks for form_post by default, so this is the shape most .NET
        // integrations actually receive.
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&response_mode=form_post&state=fp", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains($"""<form method="post" action="{RedirectUri}">""", html, StringComparison.Ordinal);
        Assert.Contains("""name="code" """, html, StringComparison.Ordinal);
        Assert.Contains("""name="state" value="fp" """, html, StringComparison.Ordinal);
    }

    private async Task<(string Code, string State)> Authorize()
    {
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=s", Ct);

        var query = System.Web.HttpUtility.ParseQueryString(
            response.Headers.Location!.ToString().Split('?')[1]);
        return (query["code"]!, query["state"]!);
    }

    private Task<HttpResponseMessage> Redeem(string code) =>
        _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", RedirectUri),
            new("client_id", CodeClient),
            new("client_secret", "any"),
        ]), Ct);
}
