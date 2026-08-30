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

    private async Task<JsonDocument> SignIn(string scope)
    {
        var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(scope)}" +
            $"&state=s&nonce=n", Ct);

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
            RecordedShape("CAP-020", "token", "id_token.payload.json"),
            Shape(payload.RootElement));
    }

    [Fact]
    public async Task The_id_token_header_matches_the_recorded_one()
    {
        using var response = await SignIn("openid mitid");
        using var header = JsonDocument.Parse(
            Base64Url.Decode(response.RootElement.GetProperty("id_token").GetString()!.Split('.')[0]));

        Assert.Equal(
            RecordedShape("CAP-020", "token", "id_token.header.json"),
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
    public async Task The_token_response_carries_the_members_a_plain_login_returns()
    {
        // The recorded full-scope response also carries a userinfo token and a transaction
        // token, which the scopes ask for; a plain login returns these five.
        using var response = await SignIn("openid mitid");

        Assert.Equal(
            ["id_token:String", "access_token:String", "expires_in:Number", "token_type:String", "scope:String"],
            Shape(response.RootElement));
    }
}
