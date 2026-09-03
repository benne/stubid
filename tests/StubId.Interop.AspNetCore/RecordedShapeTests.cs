using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Wire;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What StubID emits is compared against a recording of the real broker, member by member.
/// </summary>
/// <remarks>
/// Names, order and JSON types only: the values are the recorded identity's and cannot match.
/// This is the check that would have caught the eight ways the first id_token was wrong, none
/// of which any client library objects to.
/// </remarks>
public class RecordedShapeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private readonly HttpClient _client;

    public RecordedShapeTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    /// <summary>Member names in order, each with the JSON type of its value.</summary>
    private static List<string> Shape(JsonElement element) =>
        [.. element.EnumerateObject().Select(m => $"{m.Name}:{m.Value.ValueKind}")];

    private static List<string> RecordedShape(params string[] path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine([Root(), "fixtures", "neb", "pp-session", .. path])));
        return Shape(document.RootElement);
    }

    private async Task<JsonDocument> SignIn(string scope, string? identityProviderParameters = null)
    {
        var extra = identityProviderParameters is null
            ? ""
            : $"&idp_values=mitid&idp_params={Uri.EscapeDataString(identityProviderParameters)}";

        var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(scope)}" +
            $"&state=s&nonce=n{extra}", Ct);

        var code = System.Web.HttpUtility
            .ParseQueryString(authorize.Headers.Location!.ToString().Split('?')[1])["code"]!;

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", RedirectUri),
            new("client_id", CodeClient),
            new("client_secret", "any"),
        ]), Ct);

        return JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_id_token_carries_the_recorded_members_in_the_recorded_order()
    {
        using var response = await SignIn("openid mitid");
        using var payload = JsonDocument.Parse(
            Base64Url.Decode(response.RootElement.GetProperty("id_token").GetString()!.Split('.')[1]));

        Assert.Equal(
            RecordedShape("CAP-024", "token", "id_token.payload.json"),
            Shape(payload.RootElement));
    }

    [Fact]
    public async Task The_id_token_header_matches_the_recorded_one()
    {
        using var response = await SignIn("openid mitid");
        using var header = JsonDocument.Parse(
            Base64Url.Decode(response.RootElement.GetProperty("id_token").GetString()!.Split('.')[0]));

        Assert.Equal(
            RecordedShape("CAP-024", "token", "id_token.header.json"),
            Shape(header.RootElement));
    }

    [Fact]
    public async Task Userinfo_carries_the_recorded_members_in_the_recorded_order()
    {
        using var response = await SignIn("openid mitid");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", response.RootElement.GetProperty("access_token").GetString());

        using var userinfo = await _client.SendAsync(request, Ct);
        using var claims = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync(Ct));

        Assert.Equal(
            RecordedShape("CAP-020", "userinfo", "response.raw"),
            Shape(claims.RootElement));
    }

    [Fact]
    public async Task Userinfo_returns_a_reference_text_whole_in_the_recorded_slot()
    {
        // CAP-022 sent a MitID reference text in idp_params and got it back here undecoded,
        // between mitid.psd2 and mitid.geo_ip_distance_km, with no type and no digest beside it.
        // That is the opposite of what this endpoint does with a transaction text, where the
        // digest comes over and the text does not - so the slot and the wholeness are both the
        // contract rather than an accident of one recording.
        using var response = await SignIn(
            "openid mitid transaction_token",
            """{"mitid":{"reference_text":"U3R1YklEIHJlZmVyZW5jZSB0ZXh0"}}""");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", response.RootElement.GetProperty("access_token").GetString());

        using var userinfo = await _client.SendAsync(request, Ct);
        using var claims = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync(Ct));

        Assert.Equal(
            RecordedShape("CAP-022", "userinfo", "response.raw"),
            Shape(claims.RootElement));

        // The value travels as it was sent. The broker does not decode it and neither does this.
        Assert.Equal(
            "U3R1YklEIHJlZmVyZW5jZSB0ZXh0",
            claims.RootElement.GetProperty("mitid.reference_text").GetString());
    }

    [Fact]
    public async Task Userinfo_carries_no_reference_text_when_none_was_sent()
    {
        // CAP-020 sent no idp_params at all; CAP-024 sent one carrying a mitid section with a
        // loa_value and no reference_text. Neither userinfo response has the member, so it is
        // conditional on the reference text rather than on idp_params being there.
        using var response = await SignIn("openid mitid");

        // The second half of that, driven: a mitid section with something else in it.
        using var other = await SignIn("openid mitid", """{"mitid":{"loa_value":"low"}}""");

        foreach (var body in new[] { response, other })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", body.RootElement.GetProperty("access_token").GetString());

            using var userinfo = await _client.SendAsync(request, Ct);
            using var claims = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync(Ct));

            Assert.False(claims.RootElement.TryGetProperty("mitid.reference_text", out _));
        }
    }

    [Fact]
    public async Task Every_userinfo_value_is_a_string()
    {
        // The broker sends the age and both flags as strings. A client that parses either as a
        // number or a boolean works against StubID and fails against the broker.
        using var response = await SignIn("openid mitid");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", response.RootElement.GetProperty("access_token").GetString());

        using var userinfo = await _client.SendAsync(request, Ct);
        using var claims = JsonDocument.Parse(await userinfo.Content.ReadAsStringAsync(Ct));

        Assert.All(
            claims.RootElement.EnumerateObject(),
            m => Assert.Equal(JsonValueKind.String, m.Value.ValueKind));
    }

    [Fact]
    public async Task The_token_response_carries_the_recorded_members()
    {
        // The recorded response arrived with scope "openid mitid" and still carried a
        // userinfo token, so that member comes from a per-client setting rather than from the
        // scopes. StubID emits it for the same reason: the recording is of a client that has
        // it switched on.
        using var response = await SignIn("openid mitid");

        Assert.Equal(RecordedShape("CAP-024", "token", "response.raw"), Shape(response.RootElement));
    }
}
