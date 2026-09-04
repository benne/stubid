using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// StubID's own pages, which are for a person rather than for a test.
/// </summary>
/// <remarks>
/// Everything here runs in memory, so it joins the cross-platform matrix rather than the container
/// job: an admin page needs a browser no more than the login page does, and the two things a
/// browser could add - that the styling is legible and that a person can find their way around -
/// are not assertable anywhere.
/// <para>
/// What is assertable is the part that would be a defect: that a client-controlled string reaches
/// the page as text rather than as markup, and that a decision made here is the same decision the
/// control API makes rather than a second implementation of it.
/// </para>
/// </remarks>
public class AdminPageTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string Admin = "/_stubid/admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instance that parks a login, so there is something to decide.</summary>
    private static WebApplicationFactory<Program> Parking() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
        });

    private static HttpClient Browser(WebApplicationFactory<Program> stub) =>
        stub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> Park(HttpClient http, string state = "the-state")
    {
        using var parked = await http.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}&redirect_uri=http://localhost:5099/callback"
            + $"&response_type=code&scope=openid%20mitid&nonce=n&state={Uri.EscapeDataString(state)}",
            Ct);

        using var listed = await http.GetAsync("/_stubid/v1/sessions", Ct);
        using var sessions = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Ct));

        return sessions.RootElement[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_pages_answer_and_say_what_this_is()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        foreach (var path in new[] { Admin, $"{Admin}/sessions/{id}" })
        {
            using var page = await http.GetAsync(path, Ct);

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType?.ToString());

            // TRADEMARKS.md undertakes that no page suggests a real authentication took place.
            Assert.Contains(
                "StubID is an emulator",
                await page.Content.ReadAsStringAsync(Ct),
                StringComparison.Ordinal);

            // The state on these changes underneath the reader, and a cached table with a live
            // Approve button on it is what a back button would otherwise hand them.
            Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        }
    }

    /// <summary>
    /// The admin answers before the instance has been told its own address.
    /// </summary>
    /// <remarks>
    /// Everything that needs the issuer answers 503 until the address is set, which is the state a
    /// container is in between starting and being told its mapped port. An instance refusing every
    /// request is exactly when somebody opens these pages, so they must not be among the things
    /// that need it.
    /// </remarks>
    [Fact]
    public async Task The_pages_answer_before_the_address_is_set()
    {
        using var stub = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("StubId:PublicBaseUrl", ""));

        var http = Browser(stub);

        using var discovery = await http.GetAsync("/op/.well-known/openid-configuration", Ct);
        using var page = await http.GetAsync(Admin, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, discovery.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>
    /// A string somebody else chose reaches the page as text.
    /// </summary>
    /// <remarks>
    /// Two routes in, because they are separately dangerous: the citizen name is stored and shown
    /// on every page that lists people, and the request's state is whatever the client that
    /// started the login put there. The login page proves the same rule for the transaction text,
    /// and this page renders that through the same helper rather than a second decoder.
    /// </remarks>
    [Fact]
    public async Task A_string_the_page_did_not_choose_is_escaped()
    {
        using var stub = Parking();
        var http = Browser(stub);

        using var created = await http.PostAsJsonAsync(
            "/_stubid/v1/citizens",
            new { name = "<script>alert(1)</script>", dateOfBirth = "1990-01-01" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = await Park(http, state: "<img src=x onerror=alert(1)>");

        using var page = await http.GetAsync($"{Admin}/sessions/{id}", Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);

        Assert.DoesNotContain("<img src=x onerror", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A decision made on the page is the decision the control API makes.
    /// </summary>
    /// <remarks>
    /// This is the assertion the whole arrangement exists for. The guide promises that a manual
    /// click and an API call are one code path rather than two that agree until one changes, and
    /// the ladder naming the door it came through is what makes that checkable from outside.
    /// </remarks>
    [Fact]
    public async Task Deciding_on_the_page_is_the_decision_the_api_makes()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var approved = await http.PostAsync(
            $"{Admin}/sessions/{id}/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("citizen", "default")]),
            Ct);

        // Post, redirect, get, so a reload does not decide the same login twice.
        Assert.Equal(HttpStatusCode.SeeOther, approved.StatusCode);
        Assert.Equal($"{Admin}/sessions/{id}", approved.Headers.Location?.ToString());

        using var described = await http.GetAsync($"/_stubid/v1/sessions/{id}", Ct);
        using var session = JsonDocument.Parse(await described.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Approved", session.RootElement.GetProperty("state").GetString());

        using var explained = await http.GetAsync($"/_stubid/v1/sessions/{id}/explain", Ct);
        using var ladder = JsonDocument.Parse(await explained.Content.ReadAsStringAsync(Ct));

        var decided = ladder.RootElement.GetProperty("ladder").EnumerateArray()
            .Single(step => step.GetProperty("outcome").GetString() == "decided");

        Assert.Equal("the admin page", decided.GetProperty("name").GetString());
    }

    /// <summary>
    /// The citizen's own rule applies to a decision made by hand.
    /// </summary>
    /// <remarks>
    /// "Sign in as this person" has to mean the same thing whichever door said it, and a person
    /// set to fail is the case where a second implementation would quietly disagree.
    /// </remarks>
    [Fact]
    public async Task A_citizen_set_to_fail_fails_when_the_page_approves_them()
    {
        using var stub = Parking();
        var http = Browser(stub);

        using var created = await http.PostAsJsonAsync(
            "/_stubid/v1/citizens",
            new { name = "Refuses Always", dateOfBirth = "1970-02-03", rule = "mitid_user_aborted" },
            Ct);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var citizen = body.RootElement.GetProperty("id").GetString()!;

        var id = await Park(http);

        using var approved = await http.PostAsync(
            $"{Admin}/sessions/{id}/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("citizen", citizen)]),
            Ct);

        Assert.Equal(HttpStatusCode.SeeOther, approved.StatusCode);

        using var described = await http.GetAsync($"/_stubid/v1/sessions/{id}", Ct);
        using var session = JsonDocument.Parse(await described.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Failed", session.RootElement.GetProperty("state").GetString());
        Assert.Equal("mitid_user_aborted", session.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>Naming a citizen who is not here decides nothing, and says so.</summary>
    [Fact]
    public async Task Naming_a_citizen_who_is_not_here_decides_nothing()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var attempted = await http.PostAsync(
            $"{Admin}/sessions/{id}/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("citizen", "nobody")]),
            Ct);

        Assert.Equal(HttpStatusCode.SeeOther, attempted.StatusCode);
        Assert.Equal($"{Admin}/sessions/{id}?problem=citizen", attempted.Headers.Location?.ToString());

        using var described = await http.GetAsync($"/_stubid/v1/sessions/{id}", Ct);
        using var session = JsonDocument.Parse(await described.Content.ReadAsStringAsync(Ct));

        Assert.Equal("AwaitingApproval", session.RootElement.GetProperty("state").GetString());

        using var page = await http.GetAsync($"{Admin}/sessions/{id}?problem=citizen", Ct);

        Assert.Contains(
            "not on this instance",
            await page.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Losing the race is an outcome rather than an error, on the page as in the API.
    /// </summary>
    [Fact]
    public async Task Deciding_a_decided_login_shows_what_happened()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var first = await http.PostAsync(
            $"{Admin}/sessions/{id}/reject",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("errorCode", "mitid_timeout")]),
            Ct);

        using var second = await http.PostAsync(
            $"{Admin}/sessions/{id}/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("citizen", "default")]),
            Ct);

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, second.StatusCode);

        using var described = await http.GetAsync($"/_stubid/v1/sessions/{id}", Ct);
        using var session = JsonDocument.Parse(await described.Content.ReadAsStringAsync(Ct));

        // The first decision stands. A decision is written once.
        Assert.Equal("Failed", session.RootElement.GetProperty("state").GetString());
        Assert.Equal("mitid_timeout", session.RootElement.GetProperty("errorCode").GetString());

        // And the page the browser lands on says so, rather than offering the buttons again.
        using var page = await http.GetAsync($"{Admin}/sessions/{id}", Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Contains("already decided", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<button type=\"submit\">Approve</button>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows are served on their own, which is what keeps the table current.
    /// </summary>
    /// <remarks>
    /// A fragment rather than JSON, so the table has one renderer. Shortening an id and counting
    /// down two different deadlines are rules the server already holds, and a second copy of them
    /// in a browser would be a second thing to keep agreeing - with no test able to see it drift.
    /// </remarks>
    [Fact]
    public async Task The_rows_are_served_on_their_own()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var rows = await http.GetAsync($"{Admin}/rows", Ct);
        var fragment = await rows.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, rows.StatusCode);
        Assert.Equal("text/html; charset=utf-8", rows.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", rows.Headers.CacheControl?.ToString());

        // The rows and nothing round them: this is swapped into a page that already has a head,
        // a navigation and a heading of its own.
        Assert.Contains(id, fragment, StringComparison.Ordinal);
        Assert.StartsWith("<table>", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("<nav>", fragment, StringComparison.Ordinal);
    }

    /// <summary>The page carries the table before any script has run.</summary>
    /// <remarks>
    /// The script replaces the refreshing somebody would otherwise do by hand, and nothing else.
    /// With no JavaScript the table is still there, the Refresh link still reloads it, and the
    /// line claiming it updates on its own stays hidden - which is the only honest arrangement,
    /// because that line would be a lie in a browser that never ran the script.
    /// </remarks>
    [Fact]
    public async Task The_page_works_before_its_script_does()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var page = await http.GetAsync($"{Admin}?state=AwaitingApproval", Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Contains(id, html, StringComparison.Ordinal);
        Assert.Contains("""<div id="logins">""", html, StringComparison.Ordinal);

        // Reloading by hand keeps whatever the reader was looking at.
        Assert.Contains(
            """<a href="/_stubid/admin?state=AwaitingApproval">Refresh</a>""",
            html,
            StringComparison.Ordinal);

        // Hidden until the script reveals it, because a browser that never ran the script would
        // otherwise be told the table updates on its own when it does not.
        Assert.Contains("""<span id="live" hidden>""", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter that matches nothing says so, rather than claiming the instance is empty.
    /// </summary>
    /// <remarks>
    /// The two call for different things - start a login, or widen the filter - and telling
    /// somebody staring at an instance full of logins that there are none is how a page stops
    /// being believed.
    /// </remarks>
    [Fact]
    public async Task A_filter_that_matches_nothing_does_not_claim_the_instance_is_empty()
    {
        using var stub = Parking();
        var http = Browser(stub);

        await Park(http);

        using var page = await http.GetAsync($"{Admin}?state=Redeemed", Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Contains("No login matches that filter", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No logins yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_that_is_not_here_is_not_found()
    {
        using var stub = Parking();
        var http = Browser(stub);

        using var page = await http.GetAsync($"{Admin}/sessions/no-such-login", Ct);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    /// <summary>The clock, which nothing reported before.</summary>
    [Fact]
    public async Task The_instance_reports_its_clock()
    {
        using var stub = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ControllableClock", "true");
        });

        var http = Browser(stub);

        using var before = await http.GetAsync("/_stubid/v1/time", Ct);
        using var reading = JsonDocument.Parse(await before.Content.ReadAsStringAsync(Ct));

        Assert.True(reading.RootElement.GetProperty("controllable").GetBoolean());

        var started = reading.RootElement.GetProperty("now").GetDateTimeOffset();

        using var advanced = await http.PostAsJsonAsync(
            "/_stubid/v1/time/advance", new { seconds = 600.0 }, Ct);

        Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);

        using var after = await http.GetAsync("/_stubid/v1/time", Ct);
        using var moved = JsonDocument.Parse(await after.Content.ReadAsStringAsync(Ct));

        Assert.Equal(
            started.AddMinutes(10),
            moved.RootElement.GetProperty("now").GetDateTimeOffset());
    }

    /// <summary>The other half of the pair a refusal sends, which was not readable.</summary>
    [Fact]
    public async Task A_described_session_carries_both_halves_of_a_refusal()
    {
        using var stub = Parking();
        var http = Browser(stub);

        var id = await Park(http);

        using var refused = await http.PostAsync(
            $"{Admin}/sessions/{id}/reject",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("errorCode", "mitid_user_aborted")]),
            Ct);

        Assert.Equal(HttpStatusCode.SeeOther, refused.StatusCode);

        using var described = await http.GetAsync($"/_stubid/v1/sessions/{id}", Ct);
        using var session = JsonDocument.Parse(await described.Content.ReadAsStringAsync(Ct));

        Assert.Equal("mitid_user_aborted", session.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("access_denied", session.RootElement.GetProperty("oauthError").GetString());

        // The token the transition is guarded by, so a caller can tell one read from the next.
        Assert.True(session.RootElement.GetProperty("version").GetInt32() > 0);
    }
}
