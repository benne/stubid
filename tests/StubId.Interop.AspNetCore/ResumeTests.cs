extern alias harness;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Signer = harness::StubId.CaptureHarness.RequestObject;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// A login that parked and was then decided returns the browser to the client.
/// </summary>
/// <remarks>
/// <para>
/// It used to render a page saying "Return to the application" and stop there, so every guide
/// in this repository told readers to queue an outcome rather than click through, and the
/// browser matrix — the suite that exists because a real MitID login cannot be automated —
/// never navigated to the login page at all.
/// </para>
/// <para>
/// The arrival-shape cases are the ones that earn their place. The session was carrying the
/// query it parked with, and the query is enough on a plain GET and a signed request object and
/// useless on the other two: a form POST leaves it empty, and a pushed request leaves a
/// reference already redeemed. Anything built on replaying that query passes half of these.
/// </para>
/// </remarks>
public class ResumeTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";
    private const string Authority = "http://localhost/op";

    // Excluded by the credential guard's own negative lookahead; StubID checks no signature.
    private const string Password = "not-a-real-secret";

    private static readonly DateTimeOffset Issued = new(2026, 9, 2, 9, 45, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instance that parks, with a clock a test can move.</summary>
    private static WebApplicationFactory<Program> Manual() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
            b.UseSetting("StubId:ControllableClock", "true");
        });

    private static HttpClient Browser(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Parked(HttpResponseMessage authorize)
    {
        var location = authorize.Headers.Location!.ToString();

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("/op/Login?session=", location, StringComparison.Ordinal);

        return location.Split("session=")[1];
    }

    private static string Query(string extra = "", string mode = "") =>
        $"client_id={CodeClient}&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
        $"&state=s&nonce=n{(mode.Length > 0 ? $"&response_mode={mode}" : "")}{extra}";

    private static Task<HttpResponseMessage> Authorize(HttpClient client, string extra = "") =>
        client.GetAsync($"/op/connect/authorize?{Query(extra)}", Ct);

    private static Task<HttpResponseMessage> Submit(
        HttpClient client, string session, string decision, string citizen = "default") =>
        client.PostAsync($"/op/Login?session={session}", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("decision", decision),
            new KeyValuePair<string, string>("citizen", citizen),
        ]), Ct);

    private static System.Collections.Specialized.NameValueCollection Returned(
        HttpResponseMessage response)
    {
        var location = response.Headers.Location!.ToString();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);

        return System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
    }

    private static async Task<string> StateOf(HttpClient client, string session)
    {
        using var found = await client.GetAsync($"/_stubid/v1/sessions/{session}", Ct);
        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync(Ct));

        return body.RootElement.GetProperty("state").GetString()!;
    }

    [Fact]
    public async Task A_login_approved_on_the_page_carries_the_browser_back_to_the_client()
    {
        // The whole point. This answered 200 with a page telling the person to navigate back by
        // hand, which is what "a login that parks cannot be resumed" meant.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var approved = await Submit(client, session, "approve");
        var returned = Returned(approved);

        Assert.NotNull(returned["code"]);
        Assert.Equal("s", returned["state"]);

        // Collected, not merely decided.
        Assert.Equal("Redeemed", await StateOf(client, session));
    }

    [Fact]
    public async Task The_code_a_page_approval_issued_is_redeemable()
    {
        // A code-shaped string in a redirect proves nothing. This exchanges it.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        using var approved = await Submit(client, Parked(authorize), "approve");

        using var token = await client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", Returned(approved)["code"]!),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));

        Assert.True(body.RootElement.TryGetProperty("id_token", out _));
        Assert.True(body.RootElement.TryGetProperty("access_token", out _));
    }

    [Fact]
    public async Task A_login_aborted_on_the_page_carries_the_browser_back_as_a_refusal()
    {
        // CAP-023's shape, reached by clicking rather than by queueing.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        using var aborted = await Submit(client, Parked(authorize), "reject");
        var returned = Returned(aborted);

        Assert.Equal("access_denied", returned["error"]);
        Assert.Equal("mitid_user_aborted", returned["error_description"]);
        Assert.Equal("s", returned["state"]);
        Assert.Null(returned["iss"]);
    }

    [Fact]
    public async Task A_form_posted_authorize_resumes()
    {
        // The shape whose query is empty, because the parameters were in the body. A session
        // carrying the query it parked with has nothing at all to resume from here.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await client.PostAsync("/op/connect/authorize", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("scope", "openid mitid"),
            new KeyValuePair<string, string>("state", "s"),
            new KeyValuePair<string, string>("nonce", "n"),
        ]), Ct);

        using var approved = await Submit(client, Parked(authorize), "approve");

        Assert.NotNull(Returned(approved)["code"]);
    }

    [Fact]
    public async Task A_pushed_request_resumes()
    {
        // The other shape a query cannot answer for: the reference was consumed on the way in,
        // so replaying the query would answer "Unknown or expired request_uri".
        await using var factory = Manual();
        using var client = Browser(factory);

        using var pushed = await client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("scope", "openid mitid"),
            new KeyValuePair<string, string>("state", "s"),
        ]), Ct);

        using var reference = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&request_uri="
            + Uri.EscapeDataString(reference.RootElement.GetProperty("request_uri").GetString()!), Ct);

        using var approved = await Submit(client, Parked(authorize), "approve");
        var returned = Returned(approved);

        Assert.NotNull(returned["code"]);
        Assert.Equal("s", returned["state"]);
    }

    [Fact]
    public async Task A_signed_request_resumes()
    {
        // CAP-031's route. The parameters are inside a JWS rather than in the query.
        await using var factory = Manual();
        using var client = Browser(factory);

        var request = Signer.Build(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = CodeClient,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "openid mitid",
                ["state"] = "CAP-031",
                ["nonce"] = "n",
            },
            CodeClient, Authority, Password, Issued);

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code"
            + $"&request={Uri.EscapeDataString(request)}", Ct);

        using var approved = await Submit(client, Parked(authorize), "approve");
        var returned = Returned(approved);

        Assert.NotNull(returned["code"]);
        Assert.Equal("CAP-031", returned["state"]);
    }

    [Fact]
    public async Task Resuming_decides_the_login_that_parked_rather_than_starting_another()
    {
        // The failure a replay would produce quietly: re-entering authorize parks a second
        // session and re-runs the ladder, which can spend a decision queued for a different
        // login. One session, and it is the one that was collected.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var approved = await Submit(client, session, "approve");
        Assert.NotNull(Returned(approved)["code"]);

        using var listed = await client.GetAsync("/_stubid/v1/sessions", Ct);
        using var sessions = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));

        var only = Assert.Single(sessions.RootElement.EnumerateArray());

        Assert.Equal(session, only.GetProperty("id").GetString());
        Assert.Equal("Redeemed", only.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_second_submit_issues_no_second_code()
    {
        // One login, one code. Redeeming used to happen after the code was issued and its
        // answer was thrown away, which was harmless only because nothing could ask twice.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var first = await Submit(client, session, "approve");
        Assert.NotNull(Returned(first)["code"]);

        using var second = await Submit(client, session, "approve");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Null(second.Headers.Location);
        Assert.Contains("already returned to the application",
            await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_submits_at_once_issue_one_code_between_them()
    {
        // The sequential case above is answered by the dispatch, which reads the session's
        // state and finds it already collected. This is the case only the gate answers: both
        // requests read Approved, both are inside the completion at once, and exactly one of
        // them may come out with a code. Redeeming used to happen after the code was issued
        // and its answer was thrown away, which lets both mint one.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        var submissions = await Task.WhenAll(
            Submit(client, session, "approve"),
            Submit(client, session, "approve"));

        try
        {
            var codes = submissions
                .Where(r => r.StatusCode == HttpStatusCode.Redirect)
                .Select(r => System.Web.HttpUtility
                    .ParseQueryString(new Uri(r.Headers.Location!.ToString()).Query)["code"])
                .Where(c => c is not null)
                .ToList();

            Assert.Single(codes);
        }
        finally
        {
            foreach (var response in submissions)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task An_approval_made_through_the_api_returns_the_browser_when_it_comes_back()
    {
        // The page and the control API are one path all the way to the callback now, not only
        // as far as the decision.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { }, Ct);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var back = await client.GetAsync($"/op/Login?session={session}", Ct);

        Assert.NotNull(Returned(back)["code"]);
    }

    [Fact]
    public async Task A_login_that_expired_while_it_waited_returns_the_browser_with_a_timeout()
    {
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 301 }, Ct);

        using var back = await client.GetAsync($"/op/Login?session={session}", Ct);
        var returned = Returned(back);

        Assert.Equal("access_denied", returned["error"]);
        Assert.Equal("mitid_timeout", returned["error_description"]);
    }

    [Fact]
    public async Task An_approval_nobody_collects_stops_handing_out_a_code()
    {
        // The second window. Approving stops the first deadline mattering, so before a login
        // could be resumed an approved session stayed in the store for the life of the
        // instance - invisible, because nothing ever collected one.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { }, Ct);

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 301 }, Ct);

        using var back = await client.GetAsync($"/op/Login?session={session}", Ct);

        Assert.Equal("mitid_timeout", Returned(back)["error_description"]);
    }

    [Fact]
    public async Task A_citizen_deleted_between_approval_and_collection_refuses_rather_than_crashing()
    {
        // Deciding and collecting used to happen in one request, so the citizen could not
        // vanish in between. Now it can, and an unhandled exception on the way back to a client
        // is an empty 500 with no callback at all.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var created = await client.PostAsJsonAsync("/_stubid/v1/citizens",
            new { name = "Vanishing Person", dateOfBirth = "1990-02-11", id = "vanishes" }, Ct);

        using var authorize = await Authorize(client);
        var session = Parked(authorize);

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { citizenId = "vanishes" }, Ct);

        using var deleted = await client.DeleteAsync("/_stubid/v1/citizens/vanishes", Ct);

        using var back = await client.GetAsync($"/op/Login?session={session}", Ct);

        Assert.Equal("mitid_identity_not_found", Returned(back)["error_description"]);

        // Nothing was collected, so the session says so and a recreated citizen would work.
        Assert.Equal("Approved", await StateOf(client, session));
    }

    [Fact]
    public async Task A_client_that_asked_for_a_fragment_is_refused_in_the_fragment()
    {
        // The success path honoured all three response modes and the refusal path answered
        // every one of them with a query, so a client reading the fragment saw nothing.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?{Query(mode: "fragment")}", Ct);

        using var aborted = await Submit(client, Parked(authorize), "reject");
        var location = aborted.Headers.Location!.ToString();

        Assert.StartsWith($"{RedirectUri}#", location, StringComparison.Ordinal);
        Assert.Contains("error=access_denied", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_mode_nothing_supports_collects_nothing()
    {
        // Nothing validates response_mode on the way in, so this reached the end of the
        // completion, minted a code and then answered with an error page - leaving the session
        // saying a code had been collected and the code itself in a store nothing evicts.
        await using var factory = Manual();
        using var client = Browser(factory);

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?{Query(mode: "carrier-pigeon")}", Ct);

        var session = Parked(authorize);

        using var approved = await Submit(client, session, "approve");

        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        Assert.Contains("/op/Error", approved.Headers.Location!.ToString(), StringComparison.Ordinal);

        Assert.Equal("Approved", await StateOf(client, session));
    }
}
