using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Wire;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The hybrid response, where an id_token arrives through the front channel alongside a code.
/// </summary>
/// <remarks>
/// Shaped from a recording made with a client on the hybrid grant. ASP.NET Core rejects a
/// front-channel id_token whose c_hash is missing or wrong, so this is not a shape anyone can
/// reason their way to.
/// </remarks>
public class HybridTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string HybridClient = "c0beb4dc-69d1-4316-8167-2d0a62816103";
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private readonly HttpClient _client;

    public HybridTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Fields(string html) => Regex
        .Matches(html, """<input type="hidden" name="([^"]+)" value="([^"]*)" />""")
        .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));

    private async Task<Dictionary<string, string>> Authorize(string clientId, string responseType)
    {
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={clientId}" +
            $"&response_type={Uri.EscapeDataString(responseType)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=h&nonce=n&response_mode=form_post", Ct);

        return Fields(await response.Content.ReadAsStringAsync(Ct));
    }

    private static JsonElement Payload(string token) =>
        JsonDocument.Parse(Base64Url.Decode(token.Split('.')[1])).RootElement.Clone();

    [Fact]
    public async Task The_front_channel_returns_a_code_and_an_id_token()
    {
        var fields = await Authorize(HybridClient, "code id_token");

        // Recorded field order: code, id_token, state, session_state - and no iss.
        Assert.Equal(["code", "id_token", "state", "session_state"], fields.Keys);
    }

    [Fact]
    public async Task The_front_channel_token_covers_the_code_with_c_hash()
    {
        var fields = await Authorize(HybridClient, "code id_token");
        var payload = Payload(fields["id_token"]);

        Assert.Equal(HashClaims.Compute(fields["code"]), payload.GetProperty("c_hash").GetString());
        Assert.False(payload.TryGetProperty("at_hash", out _));
    }

    [Fact]
    public async Task The_back_channel_token_covers_the_access_token_instead()
    {
        var fields = await Authorize(HybridClient, "code id_token");

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", fields["code"]),
            new("redirect_uri", RedirectUri),
            new("client_id", HybridClient),
            new("client_secret", "any"),
        ]), Ct);

        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        var payload = Payload(body.RootElement.GetProperty("id_token").GetString()!);

        Assert.Equal(
            HashClaims.Compute(body.RootElement.GetProperty("access_token").GetString()!),
            payload.GetProperty("at_hash").GetString());
        Assert.False(payload.TryGetProperty("c_hash", out _));
    }

    [Fact]
    public async Task The_hash_claim_sits_where_the_recording_puts_it()
    {
        // Both hashes occupy the same slot, after nonce and before sid.
        var fields = await Authorize(HybridClient, "code id_token");
        var members = Payload(fields["id_token"]).EnumerateObject().Select(m => m.Name).ToList();

        Assert.Equal("nonce", members[members.IndexOf("c_hash") - 1]);
        Assert.Equal("sid", members[members.IndexOf("c_hash") + 1]);
    }

    [Fact]
    public async Task A_code_only_response_carries_iss_and_no_id_token()
    {
        // iss is advertised in discovery, but the broker omits it whenever an id_token is
        // returned, since that already carries the issuer.
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=h", Ct);

        var query = System.Web.HttpUtility.ParseQueryString(
            response.Headers.Location!.ToString().Split('?')[1]);

        Assert.Equal(
            new[] { "code", "state", "session_state", "iss" },
            query.AllKeys.Select(k => k ?? "").ToArray());
    }

    [Fact]
    public async Task A_client_cannot_ask_for_a_response_type_it_is_not_registered_for()
    {
        // The code client asking for a hybrid response is refused, and never redirected back.
        var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code%20id_token" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=h", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/op/Error?errorId=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_type_order_does_not_matter()
    {
        // The broker's own hybrid client declares "id_token code" while client libraries send
        // "code id_token"; the comparison ignores order, as the broker's does.
        var fields = await Authorize(HybridClient, "id_token code");

        Assert.True(fields.ContainsKey("id_token"));
        Assert.True(fields.ContainsKey("code"));
    }
}
