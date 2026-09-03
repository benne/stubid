using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The parts of the broker's surface that no unattended recording reaches, because getting
/// there needs a completed login.
/// </summary>
public class RequestSurfaceTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Instance(bool automatic = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", automatic ? "true" : "false");
        });

    /// <summary>Signs in and returns the token response, which is where everything else starts.</summary>
    private static async Task<JsonElement> SignIn(HttpClient client, string scope = "openid mitid")
    {
        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(scope)}" +
            "&state=s&nonce=n", Ct);

        var code = System.Web.HttpUtility
            .ParseQueryString(new Uri(authorize.Headers.Location!.ToString()).Query)["code"];

        using var token = await client.PostAsync("/op/connect/token", new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code!),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
        ]), Ct);

        return JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> MatchCpr(HttpClient client, string accessToken, string? cpr)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/op/api/v1/mitid/matchCpr")
        {
            Content = JsonContent.Create(cpr is null ? new { } : (object)new { cpr }),
        };

        request.Headers.Authorization = new("Bearer", accessToken);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>An instance whose clock a test can move, for the two lifetimes worth reaching.</summary>
    private static WebApplicationFactory<Program> WithAClock() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ControllableClock", "true");
        });

    private static async Task<string> Push(HttpClient client)
    {
        using var pushed = await client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("scope", "openid mitid"),
            new KeyValuePair<string, string>("state", "s"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        using var body = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));

        // The number the client is told, which is also the one it is held to.
        Assert.Equal(600, body.RootElement.GetProperty("expires_in").GetInt32());

        return body.RootElement.GetProperty("request_uri").GetString()!;
    }

    private static Task<HttpResponseMessage> Redeem(HttpClient client, string requestUri) =>
        client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}"
            + $"&request_uri={Uri.EscapeDataString(requestUri)}", Ct);

    [Fact]
    public async Task A_pushed_request_is_redeemable_until_the_expiry_it_was_promised()
    {
        // Nine minutes in, the reference still works: the lifetime is enforced at the moment it
        // says and not a moment earlier.
        await using var factory = WithAClock();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var requestUri = await Push(client);

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 540 }, Ct);

        using var redeemed = await Redeem(client, requestUri);

        Assert.StartsWith(RedirectUri, redeemed.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("code=", redeemed.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pushed_request_left_past_its_expiry_is_refused()
    {
        // The endpoint advertised expires_in: 600 from the day it was written and nothing
        // enforced it, so a reference worked for the life of the instance. The refusal is the
        // broker's own error page rather than anything the client sees, which is what an
        // unusable request_uri has always earned here.
        await using var factory = WithAClock();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var requestUri = await Push(client);

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 601 }, Ct);

        using var redeemed = await Redeem(client, requestUri);

        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        Assert.Contains("/op/Error", redeemed.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_reference_is_refused_even_once_other_pushes_have_happened()
    {
        // Deliberately not a test of the sweep. Pushing drops entries whose time has passed, so
        // an abandoned push is not held for the life of the instance - but that is invisible
        // from outside, because a stale reference is refused on its own merits whether or not
        // anything swept it. What this pins is that the sweeping does not take the wrong one:
        // the fresh reference still works afterwards.
        await using var factory = WithAClock();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var abandoned = await Push(client);

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 601 }, Ct);

        // A second push, which is what does the sweeping.
        var fresh = await Push(client);

        using var stale = await Redeem(client, abandoned);
        Assert.Contains("/op/Error", stale.Headers.Location!.ToString(), StringComparison.Ordinal);

        using var usable = await Redeem(client, fresh);
        Assert.StartsWith(RedirectUri, usable.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_personal_number_is_matched_without_ever_being_disclosed()
    {
        // The point of the flow: a private service provider may not ask for a personal number,
        // so it submits the one it holds and is told yes or no.
        await using var factory = Instance();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await SignIn(client);
        var accessToken = token.GetProperty("access_token").GetString()!;

        using var citizens = await client.GetAsync("/_stubid/v1/citizens", Ct);
        using var listed = JsonDocument.Parse(await citizens.Content.ReadAsStringAsync(Ct));
        var cpr = listed.RootElement[0].GetProperty("cpr").GetString()!;

        using var right = await MatchCpr(client, accessToken, cpr);

        // A replacement number too, so the wrong answer is not somebody else's real one.
        using var wrong = await MatchCpr(client, accessToken, "9112990001");

        // A JSON boolean. Everything on the userinfo side is a string, so this is worth
        // asserting rather than assuming.
        Assert.Equal(JsonValueKind.True,
            JsonDocument.Parse(await right.Content.ReadAsStringAsync(Ct))
                .RootElement.GetProperty("cprNumberMatch").ValueKind);

        Assert.Equal(JsonValueKind.False,
            JsonDocument.Parse(await wrong.Content.ReadAsStringAsync(Ct))
                .RootElement.GetProperty("cprNumberMatch").ValueKind);
    }

    [Fact]
    public async Task The_fourth_attempt_in_a_session_is_refused()
    {
        // A suite that passes here and fails on the fourth call against the broker has been
        // told nothing useful, so the limit is real rather than configuration.
        await using var factory = Instance();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var accessToken = (await SignIn(client)).GetProperty("access_token").GetString()!;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var allowed = await MatchCpr(client, accessToken, "9112990001");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await MatchCpr(client, accessToken, "9112990001");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "Cpr Match exceeded. Only 3 tries is allowed within a session.",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct))
                .RootElement.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task A_call_with_no_personal_number_is_told_which_one_is_missing()
    {
        await using var factory = Instance();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var accessToken = (await SignIn(client)).GetProperty("access_token").GetString()!;

        using var response = await MatchCpr(client, accessToken, cpr: null);

        // Its own envelope, not an OAuth error: a different endpoint family on the same host.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"errorMessage":"Missing Cpr parameter"}""",
            (await response.Content.ReadAsStringAsync(Ct)).Trim());
    }

    [Fact]
    public async Task Ending_a_session_from_the_back_channel_kills_the_token()
    {
        await using var factory = Instance();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var accessToken = (await SignIn(client)).GetProperty("access_token").GetString()!;

        using var before = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        before.Headers.Authorization = new("Bearer", accessToken);
        using var worked = await client.SendAsync(before, Ct);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/op/api/v1/session/logout");
        logout.Headers.Authorization = new("Bearer", accessToken);
        using var ended = await client.SendAsync(logout, Ct);

        using var after = new HttpRequestMessage(HttpMethod.Get, "/op/connect/userinfo");
        after.Headers.Authorization = new("Bearer", accessToken);
        using var dead = await client.SendAsync(after, Ct);

        Assert.Equal(HttpStatusCode.OK, worked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        // A test that wants to prove its cleanup ran needs the token to actually stop working.
        Assert.Equal(HttpStatusCode.Unauthorized, dead.StatusCode);
    }

    [Fact]
    public async Task End_session_honours_a_redirect_only_when_it_is_given_a_hint()
    {
        await using var factory = Instance();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var idToken = (await SignIn(client)).GetProperty("id_token").GetString()!;
        var logout = $"post_logout_redirect_uri={Uri.EscapeDataString(RedirectUri)}&state=bye";

        using var withHint = await client.GetAsync(
            $"/op/connect/endsession?id_token_hint={idToken}&{logout}", Ct);

        // CAP-045: without one, the same request goes to the broker's own page and the
        // redirect is ignored. That is why a client that omits the hint never comes back.
        using var without = await client.GetAsync($"/op/connect/endsession?{logout}", Ct);

        Assert.Equal($"{RedirectUri}?state=bye", withHint.Headers.Location!.ToString());
        Assert.EndsWith("/op/Account/Logout", without.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silent_login_that_would_have_to_ask_says_so_instead_of_asking()
    {
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=s&prompt=none", Ct);

        var location = response.Headers.Location!.ToString();

        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("error=login_required", location, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mitid")]
    [InlineData("mitid_erhverv")]
    public async Task An_identity_provider_the_broker_names_is_accepted(string idp)
    {
        // Refusing a value the broker accepts is the worse failure of the two: it fails a
        // request that works in production, and the trail leads to StubID rather than away.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=s&nonce=n&idp_values={idp}", Ct);

        Assert.DoesNotContain("/op/Error", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Values that parse as JSON and are not an object, plus two that parse and cannot be read.
    /// </summary>
    /// <remarks>
    /// CAP-040 recorded <c>idp_params</c> that is not JSON at all being refused with the broker's
    /// own error page. These are the neighbouring class, and they were what turned that refusal
    /// into an empty 500 the first time this parameter was read: <c>TryGetProperty</c> throws
    /// rather than answering false when the root is not an object, an unpaired surrogate escape
    /// parses and throws when the string is materialised, and a repeated member throws when the
    /// section is collected. None of them is a JsonException, so none was caught.
    /// </remarks>
    public static TheoryData<string> IdpParamsThatIsNotAnObject() =>
    [
        "null",
        "true",
        "123",
        "\"a string\"",
        "[1,2]",
        """[{"mitid":{"reference_text":"U3R1YklE"}}]""",
        """{"mitid":{"reference_text":"\ud800"}}""",
        """{"mitid":{"reference_text":"a","reference_text":"b"}}""",
    ];

    [Theory]
    [MemberData(nameof(IdpParamsThatIsNotAnObject))]
    public async Task An_idp_params_that_is_not_a_usable_object_is_answered_not_crashed(string raw)
    {
        // Whatever the answer is, it is an answer. An empty 500 is the one thing the broker never
        // sends, and it is what a client sees as "my callback never fired" with nothing to go on.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=s&nonce=n&idp_values=mitid&idp_params={Uri.EscapeDataString(raw)}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(IdpParamsThatIsNotAnObject))]
    public async Task A_pushed_idp_params_that_is_not_a_usable_object_is_answered_not_crashed(
        string raw)
    {
        // The same values through the push, which parses the form with the same reader and has
        // no error page to fall back to.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("scope", "openid mitid"),
            new KeyValuePair<string, string>("idp_params", raw),
        ]), Ct);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_value_inside_idp_params_is_carried_rather_than_refused()
    {
        // CAP-010. The broker publishes an error code for a malformed uuid_hint, which is
        // proof it is raised later, in the flow, rather than here.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var idpParams = Uri.EscapeDataString("""{"mitid":{"uuid_hint":"not-a-uuid"}}""");

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=s&nonce=n&idp_values=mitid&idp_params={idpParams}", Ct);

        Assert.DoesNotContain("/op/Error", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_simulated_login_needs_no_page_and_names_who_it_signed_in_as()
    {
        // The incumbent's published grammar, so a suite already paying for it points at
        // StubID and changes nothing else.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var created = await client.PostAsJsonAsync("/_stubid/v1/citizens",
            new { name = "Simulated Person", dateOfBirth = "1988-06-14", id = "sim" }, Ct);
        using var citizen = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var uuid = citizen.RootElement.GetProperty("uuid").GetString();

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=s&nonce=n" +
            $"&simulation={Uri.EscapeDataString($"no-ui uuid:{uuid}")}", Ct);

        var location = response.Headers.Location!.ToString();

        // Straight back to the client with a code, with no page rendered in between.
        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("code=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_login_is_reported_the_way_a_recorded_one_was()
    {
        // CAP-023, member for member. A client that reads session_state on success and not on
        // failure would work against StubID and break against the broker, or the reverse.
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var queued = await client.PostAsJsonAsync("/_stubid/v1/behaviours/enqueue",
            new { approve = false, errorCode = "mitid_user_aborted" }, Ct);

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=CAP-023&nonce=n", Ct);

        var returned = System.Web.HttpUtility.ParseQueryString(
            new Uri(response.Headers.Location!.ToString()).Query);

        var recorded = await File.ReadAllLinesAsync(Path.Combine(
            Root(), "fixtures", "neb", "pp-session", "CAP-023", "callback", "response.raw"), Ct);

        Assert.Equal(
            recorded.Where(l => l.Length > 0).Select(l => l.Split('=')[0]).Order(),
            returned.AllKeys.Select(k => k!).Order());

        Assert.Equal("access_denied", returned["error"]);
        Assert.Equal("mitid_user_aborted", returned["error_description"]);
        Assert.Equal("CAP-023", returned["state"]);

        // Advertised in discovery, and still absent here. The recording is what settles it.
        Assert.Null(returned["iss"]);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    [Fact]
    public async Task A_simulation_naming_nobody_fails_with_the_brokers_own_code()
    {
        await using var factory = Instance(automatic: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state=s&nonce=n" +
            $"&simulation={Uri.EscapeDataString("no-ui uuid:11111111-2222-3333-4444-555555555555")}", Ct);

        Assert.Contains("error_description=mitid_simulation_unknown_user",
            response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }
}
