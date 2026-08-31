using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What a test in someone else's continuous integration actually calls.
/// </summary>
public class ControlApiTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instance that waits for someone to decide, as one with a person watching does.</summary>
    private static WebApplicationFactory<Program> Manual() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
            b.UseSetting("StubId:ControllableClock", "true");
        });

    private static Task<HttpResponseMessage> Authorize(HttpClient client, string state = "s") =>
        client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid&state={state}", Ct);

    [Fact]
    public async Task A_login_waits_where_a_person_can_act_on_it()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);

        // Not back to the client: nobody has decided anything yet.
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("/op/Login?session=", authorize.Headers.Location!.ToString(), StringComparison.Ordinal);

        using var listed = await client.GetAsync("/_stubid/v1/sessions?state=AwaitingApproval", Ct);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));

        Assert.Equal(1, body.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_waiting_login_is_completed_from_a_test()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { citizenId = "default" }, Ct);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Approved", await StateOf(client, session));
    }

    [Fact]
    public async Task A_second_decision_is_refused_and_told_what_happened()
    {
        // A person clicking as a test rejects is the ordinary case, not an edge case. The
        // loser needs the outcome, not an exception.
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var first = await client.PostAsJsonAsync($"/_stubid/v1/sessions/{session}/approve", new { }, Ct);
        using var second = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/reject", new { errorCode = "mitid_user_aborted" }, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Approved", body.RootElement.GetProperty("outcome").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_queued_refusal_makes_the_next_login_fail_with_the_brokers_own_code()
    {
        // The failure story: no broker sells "make this login abort, now, repeatably".
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var queued = await client.PostAsJsonAsync("/_stubid/v1/behaviours/enqueue",
            new { approve = false, clientId = CodeClient, errorCode = "mitid_user_aborted" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);

        using var authorize = await Authorize(client);
        var location = authorize.Headers.Location!.ToString();

        // Reported back to the client, as a user-level failure is.
        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("error=access_denied", location, StringComparison.Ordinal);
        Assert.Contains("error_description=mitid_user_aborted", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_five_minute_timeout_is_reached_without_waiting_five_minutes()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var advanced = await client.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 301 }, Ct);
        Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);

        Assert.Equal("Expired", await StateOf(client, session));
    }

    [Fact]
    public async Task The_ladder_explains_itself_including_the_tiers_it_skipped()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var explained = await client.GetAsync($"/_stubid/v1/sessions/{session}/explain", Ct);
        using var body = JsonDocument.Parse(await explained.Content.ReadAsStringAsync(Ct));

        var ladder = body.RootElement.GetProperty("ladder").EnumerateArray().ToList();

        // Every tier reports, skipped ones included. Without that, precedence is guesswork.
        Assert.Equal(
            [2, 3, 4, 8],
            ladder.Where(s => s.GetProperty("tier").ValueKind != JsonValueKind.Null)
                .Select(s => s.GetProperty("tier").GetInt32()));

        Assert.All(ladder, s => Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("reason").GetString())));

        // Then a decision from outside joins the same list, as tier one.
        using var approved = await client.PostAsJsonAsync($"/_stubid/v1/sessions/{session}/approve", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var after = await client.GetAsync($"/_stubid/v1/sessions/{session}/explain", Ct);
        using var second = JsonDocument.Parse(await after.Content.ReadAsStringAsync(Ct));

        var decided = second.RootElement.GetProperty("ladder").EnumerateArray()
            .Single(s => s.GetProperty("outcome").GetString() == "decided");

        Assert.Equal(1, decided.GetProperty("tier").GetInt32());
    }

    [Fact]
    public async Task A_citizen_created_from_a_test_can_be_signed_in_as()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var created = await client.PostAsJsonAsync("/_stubid/v1/citizens",
            new { name = "Karen Refsgaard", dateOfBirth = "1979-11-02", gender = "female", id = "karen" }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var citizen = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));

        // Always a replacement number: the day is raised into a range no issued number uses.
        var cpr = citizen.RootElement.GetProperty("cpr").GetString()!;
        Assert.InRange(int.Parse(cpr[..2]), 61, 91);

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { citizenId = "karen" }, Ct);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task The_login_page_completes_the_same_session_the_api_would()
    {
        // The page and the control API are one code path. If they were two, a suite that
        // passes through the API would prove nothing about the click a person makes.
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var page = await client.GetAsync($"/op/Login?session={session}", Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // Nobody should be able to mistake this page for a real authenticator.
        Assert.Contains("no real authentication is taking place", html, StringComparison.Ordinal);

        using var submitted = await client.PostAsync($"/op/Login?session={session}",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("decision", "approve"),
                new KeyValuePair<string, string>("citizen", "default"),
            ]), Ct);

        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal("Approved", await StateOf(client, session));
    }

    [Fact]
    public async Task An_unknown_session_is_not_a_page()
    {
        await using var factory = Manual();
        using var client = factory.CreateClient();

        using var page = await client.GetAsync("/op/Login?session=nothing-parked-here", Ct);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task A_citizen_set_to_fail_fails_even_when_a_test_approves_them()
    {
        // Signing in as someone has to mean the same thing however that name was chosen,
        // otherwise a rule holds on one path and is quietly ignored on the other.
        await using var factory = Manual();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var created = await client.PostAsJsonAsync("/_stubid/v1/citizens", new
        {
            name = "Test Person",
            dateOfBirth = "1990-01-01",
            id = "aborts",
            rule = "mitid_user_aborted",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var authorize = await Authorize(client);
        var session = authorize.Headers.Location!.ToString().Split("session=")[1];

        using var approved = await client.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{session}/approve", new { citizenId = "aborts" }, Ct);

        // Decided, and the response says what it decided rather than claiming an approval.
        using var body = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Failed", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("Failed", await StateOf(client, session));
    }

    private static async Task<string> StateOf(HttpClient client, string session)
    {
        using var response = await client.GetAsync($"/_stubid/v1/sessions/{session}", Ct);

        // Says which session went missing. Parsing a 404's empty body instead reports a JSON
        // error at position zero, which names neither the test nor the session.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"No session {session}: the store answered {(int)response.StatusCode}.");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return body.RootElement.GetProperty("state").GetString()!;
    }
}
