using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Client;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The shipped control client, against the server it is shipped for.
/// </summary>
/// <remarks>
/// In process and without Docker, so these run on every platform CI builds on rather than only the
/// one that can start a Linux container. The containerised suite proves the container; this proves
/// the client, and the two do not need to prove it twice.
/// </remarks>
public class ControlClientTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Two hosts rather than two settings on one: automatic approval decides a login before anything
    // can look at it, and the tests about deciding one by hand need it parked.
    private readonly WebApplicationFactory<Program> _automatic = Host(factory, automatic: true);
    private readonly WebApplicationFactory<Program> _manual = Host(factory, automatic: false);

    private static WebApplicationFactory<Program> Host(
        WebApplicationFactory<Program> factory, bool automatic) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", automatic ? "true" : "false");
        });

    private StubIdClient Connect() => new(_automatic.CreateClient());

    private StubIdClient Manual() => new(_manual.CreateClient());

    [Fact]
    public async Task The_client_creates_a_citizen_and_reads_the_generated_number_back()
    {
        using var stub = Connect();

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec
            {
                Name = "Karen Refsgaard",
                DateOfBirth = new DateOnly(1979, 11, 2),
                Gender = StubIdGender.Female,
            },
            Ct);

        Assert.Equal("Karen Refsgaard", citizen.Name);
        Assert.Equal("1979-11-02", citizen.DateOfBirth);

        // A replacement number: the day of month is raised past any a real one uses, so this
        // cannot collide with a person.
        var day = int.Parse(citizen.Cpr[..2], System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(day, 61, 91);
    }

    /// <remarks>
    /// "The tester clicked approve as the timeout fired" is an ordinary event in a suite that
    /// exercises timeouts, and the usual source of a test that fails once a fortnight. The second
    /// writer learns what actually happened instead of catching something.
    /// </remarks>
    [Fact]
    public async Task A_second_decision_comes_back_as_an_outcome_and_not_an_exception()
    {
        using var stub = Manual();
        var session = await Park(stub);

        var first = await stub.Sessions.ApproveAsync(session, ct: Ct);
        var second = await stub.Sessions.RejectAsync(session, ct: Ct);

        Assert.True(first.Decided);
        Assert.False(second.Decided);
        Assert.Equal(SessionState.Approved, second.Outcome?.State);
        Assert.False(string.IsNullOrWhiteSpace(second.Detail));
    }

    /// <remarks>
    /// The trap the approve documentation warns about: the call succeeded and the login failed,
    /// because a citizen's rule is what approving that person means. A test asserting on the
    /// absence of an exception would read this as a pass.
    /// </remarks>
    [Fact]
    public async Task An_approval_the_citizens_rule_refuses_is_reported_as_a_failure()
    {
        using var stub = Manual();

        await stub.Citizens.CreateAsync(
            new CitizenSpec
            {
                Name = "Test Person",
                Id = "aborts",
                DateOfBirth = new DateOnly(1990, 1, 1),
                Rule = "mitid_user_aborted",
            },
            Ct);

        var outcome = await stub.Sessions.ApproveAsync(await Park(stub), "aborts", Ct);

        Assert.True(outcome.Decided);
        Assert.Equal(SessionState.Failed, outcome.State);
    }

    /// <summary>
    /// The route the create response has been pointing at since it was written.
    /// </summary>
    /// <remarks>
    /// <c>POST /citizens</c> answered 201 with a Location header naming a route that did not
    /// exist, and the client carried a comment apologising for it. Both are fixed together, which
    /// is the only way that apology gets to be deleted honestly.
    /// </remarks>
    [Fact]
    public async Task A_created_citizen_can_be_read_back_from_the_route_that_was_promised()
    {
        using var stub = Connect();

        var created = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Sofie Lund", DateOfBirth = new DateOnly(1991, 6, 12) }, Ct);

        var read = await stub.Citizens.FindAsync(created.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal(created.Cpr, read.Cpr);

        Assert.Null(await stub.Citizens.FindAsync("nobody-by-that-name", Ct));
    }

    /// <summary>
    /// Changing a rule changes what approving that person means, and clearing it puts them back.
    /// </summary>
    /// <remarks>
    /// Asserted through a login rather than by reading the field back, because the field is not
    /// the point: the rule is what approving as somebody does, and a test that only checked the
    /// value would pass against a server that stored it and never consulted it.
    /// </remarks>
    [Fact]
    public async Task A_rule_can_be_set_and_cleared_while_the_instance_runs()
    {
        using var stub = Manual();

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Jonas Riis", DateOfBirth = new DateOnly(1988, 2, 2) }, Ct);

        Assert.Null(citizen.Rule);

        var refusing = await stub.Citizens.SetRuleAsync(citizen.Id, "mitid_user_aborted", Ct);

        Assert.Equal("mitid_user_aborted", refusing?.Rule);

        var refused = await stub.Sessions.ApproveAsync(await Park(stub), citizen.Id, Ct);

        Assert.Equal(SessionState.Failed, refused.State);

        var restored = await stub.Citizens.SetRuleAsync(citizen.Id, null, Ct);

        Assert.Null(restored?.Rule);

        var approved = await stub.Sessions.ApproveAsync(await Park(stub), citizen.Id, Ct);

        Assert.Equal(SessionState.Approved, approved.State);

        Assert.Null(await stub.Citizens.SetRuleAsync("nobody-by-that-name", "anything", Ct));
    }

    /// <summary>
    /// The queue can be read, and reading it does not spend it.
    /// </summary>
    /// <remarks>
    /// The second half is the one that matters. A read that dequeued would turn the tool for
    /// diagnosing a stray decision into the thing that consumes it, and the failure would look
    /// exactly like the one somebody opened the page to investigate.
    /// </remarks>
    [Fact]
    public async Task The_queue_can_be_read_without_being_spent()
    {
        using var stub = Manual();

        await stub.Behaviour.EnqueueAsync(Decision.Refused("mitid_timeout"), Ct);

        var first = await stub.Behaviour.ListAsync(Ct);
        var second = await stub.Behaviour.ListAsync(Ct);

        Assert.Equal(first.Count, second.Count);

        var queued = Assert.Single(first);

        Assert.Equal("*", queued.ClientId);
        Assert.Equal(1, queued.Position);
        Assert.False(queued.Approve);
        Assert.Equal("mitid_timeout", queued.ErrorCode);

        // And it is still there to be taken, which two reads could not prove on their own.
        var session = await stub.Sessions.FindAsync(await Drive(stub), Ct);

        Assert.Equal(SessionState.Failed, session?.State);
        Assert.Empty(await stub.Behaviour.ListAsync(Ct));
    }

    [Fact]
    public async Task Clearing_the_queue_leaves_nothing_for_the_next_login()
    {
        using var stub = Manual();

        await stub.Behaviour.EnqueueAsync(Decision.Refused("mitid_timeout"), Ct);
        await stub.Behaviour.ClearAsync(Ct);

        Assert.Empty(await stub.Behaviour.ListAsync(Ct));

        // Nothing decided it, so it waits, which is what this instance does with an undecided one.
        var session = await stub.Sessions.FindAsync(await Drive(stub), Ct);

        Assert.Equal(SessionState.AwaitingApproval, session?.State);
    }

    /// <summary>
    /// What was handed out is reported, and what it was never is.
    /// </summary>
    /// <remarks>
    /// The second half is the whole point and is asserted the only way that means anything: the
    /// code is redeemed for real tokens, and then every string this surface returns is checked
    /// against the actual code, the actual access token and the id_token. A page that leaked one
    /// would turn "see what this instance issued" into "issue yourself a token as anybody" on a
    /// surface that asks nobody who they are.
    /// </remarks>
    [Fact]
    public async Task What_was_issued_is_reported_and_never_the_value_of_it()
    {
        using var stub = Connect();
        using var browser = _automatic.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorized = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}&response_type=code"
            + "&redirect_uri=http://localhost:5099/callback&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var code = System.Web.HttpUtility
            .ParseQueryString(authorized.Headers.Location!.Query)["code"]!;

        // Before the exchange there is a code and nothing else.
        var waiting = await stub.IssuedAsync(Ct);

        Assert.Contains(waiting, artefact => artefact.Kind == "code");
        Assert.DoesNotContain(waiting, artefact => artefact.Kind == "access token");

        using var exchanged = await browser.PostAsync(
            "/op/connect/token",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", "http://localhost:5099/callback"),
                new KeyValuePair<string, string>("client_id", CodeClient),
                new KeyValuePair<string, string>("client_secret", "not-a-real-secret"),
            ]),
            Ct);

        using var tokens = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync(Ct));

        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var idToken = tokens.RootElement.GetProperty("id_token").GetString()!;

        var issued = await stub.IssuedAsync(Ct);

        // And after it there is a token and no code: one login, one code, spent.
        Assert.Contains(issued, artefact => artefact.Kind == "access token");
        Assert.DoesNotContain(issued, artefact => artefact.Kind == "code");
        Assert.All(issued, artefact => Assert.Equal(CodeClient, artefact.ClientId));

        // A login it can be lined up against, which is what a value would otherwise be used for,
        // and it has to be the login's own id rather than the broker's sid or the link is dead.
        Assert.All(
            issued.Where(artefact => artefact.Kind != "pushed request"),
            artefact => Assert.False(string.IsNullOrEmpty(artefact.LoginId)));

        var everything = string.Join(
            "\u001f",
            waiting.Concat(issued).SelectMany(artefact => new[]
            {
                artefact.Kind, artefact.ClientId, artefact.CitizenId, artefact.LoginId,
                artefact.Scope, artefact.AuthenticatedAt?.ToString(), artefact.Expires?.ToString(),
            }));

        foreach (var secret in new[] { code, accessToken, idToken })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A reset drops what was issued, and a code taken before it cannot be used after.
    /// </summary>
    /// <remarks>
    /// This is a change rather than a completion. A reset used to clear the sessions and leave
    /// the codes standing, so an instance that said it was fresh would still redeem one taken
    /// before the reset. Citizens still survive, which is the line this endpoint draws.
    /// </remarks>
    [Fact]
    public async Task A_reset_drops_what_was_issued()
    {
        using var stub = Connect();
        using var browser = _automatic.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var authorized = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}&response_type=code"
            + "&redirect_uri=http://localhost:5099/callback&scope=openid&state=s&nonce=n",
            Ct);

        var code = System.Web.HttpUtility
            .ParseQueryString(authorized.Headers.Location!.Query)["code"]!;

        Assert.NotEmpty(await stub.IssuedAsync(Ct));

        await stub.ResetAsync(Ct);

        Assert.Empty(await stub.IssuedAsync(Ct));

        using var afterwards = await browser.PostAsync(
            "/op/connect/token",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", "http://localhost:5099/callback"),
                new KeyValuePair<string, string>("client_id", CodeClient),
                new KeyValuePair<string, string>("client_secret", "not-a-real-secret"),
            ]),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, afterwards.StatusCode);
    }

    /// <summary>
    /// A suite sharing one instance can make a login park for one test and hand it back.
    /// </summary>
    /// <remarks>
    /// The setting is what the instance was started with, and this is an override in front of
    /// it, which is why null puts it back rather than turning approval off. Asserted through a
    /// login, because the ladder is what has to change.
    /// </remarks>
    [Fact]
    public async Task Automatic_approval_can_be_turned_off_and_handed_back()
    {
        using var stub = Connect();

        var started = await stub.Runtime.GetAutomaticApprovalAsync(Ct);

        Assert.True(started.Enabled);
        Assert.Null(started.Overridden);

        var manual = await stub.Runtime.SetAutomaticApprovalAsync(false, Ct);

        Assert.False(manual.Enabled);
        Assert.True(manual.Configured);
        Assert.False(manual.Overridden);

        var parked = await stub.Sessions.FindAsync(await Drive(stub, _automatic), Ct);

        Assert.Equal(SessionState.AwaitingApproval, parked?.State);

        var handedBack = await stub.Runtime.SetAutomaticApprovalAsync(null, Ct);

        Assert.True(handedBack.Enabled);
        Assert.Null(handedBack.Overridden);

        var decided = await stub.Sessions.FindAsync(await Drive(stub, _automatic), Ct);

        Assert.NotEqual(SessionState.AwaitingApproval, decided?.State);
    }

    /// <summary>The three clients, which a reader otherwise finds by grepping for a GUID.</summary>
    [Fact]
    public async Task The_client_reads_the_clients_it_is_allowed_to_use()
    {
        using var stub = Connect();

        var clients = await stub.ClientsAsync(Ct);

        Assert.Equal(3, clients.Count);
        Assert.Contains(clients, client => client.ClientId == CodeClient);
        Assert.All(clients, client => Assert.NotEmpty(client.ResponseTypes));
        Assert.All(clients, client => Assert.Equal("published-test-clients", client.Organisation));
    }

    /// <summary>
    /// The routes it reports are the routes it answers on.
    /// </summary>
    /// <remarks>
    /// The point of reading them from the loaded endpoints rather than from a list is that the
    /// list cannot go stale. Proving that means taking one of the reported paths and fetching it:
    /// a table nobody dials is a table that can quietly describe a build that no longer exists.
    /// </remarks>
    [Fact]
    public async Task The_routes_it_reports_are_the_routes_it_answers_on()
    {
        using var stub = Connect();

        var routes = await stub.RoutesAsync(Ct);
        var discovery = Assert.Single(routes, route => route.Role == "discovery");

        Assert.Contains("GET", discovery.Methods);

        using var browser = _automatic.CreateClient();
        using var answered = await browser.GetAsync(discovery.Pattern, Ct);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        // And the roles are the engine's own vocabulary, so a caller can find a route without
        // knowing the path the broker chose for it.
        Assert.Contains(routes, route => route.Role == "token");
        Assert.Contains(routes, route => route.Role?.StartsWith("extra:", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Reading the clock is a question an ordinary instance can answer.
    /// </summary>
    /// <remarks>
    /// Advancing could not stand in for this. Advancing by nothing still needs a controllable
    /// clock, so until there was a read there was no way to ask an ordinary instance what time it
    /// thought it was - which is where every argument about a timeout starts.
    /// </remarks>
    [Fact]
    public async Task The_client_reads_a_clock_it_is_not_allowed_to_move()
    {
        using var stub = Connect();

        var clock = await stub.Time.ReadAsync(Ct);

        Assert.False(clock.Controllable);
        Assert.NotEqual(default, clock.Now);
    }

    [Fact]
    public async Task Moving_time_on_an_instance_with_a_fixed_clock_says_which_setting_to_change()
    {
        using var stub = Connect();

        var refusal = await Assert.ThrowsAsync<StubIdException>(
            () => stub.Time.AdvanceAsync(TimeSpan.FromSeconds(301), Ct));

        Assert.Equal("the clock is not controllable", refusal.Error);
        Assert.Contains("ControllableClock", refusal.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_client_explains_a_login_tier_by_tier_including_the_undecided_one()
    {
        using var stub = Manual();

        var explanation = await stub.Sessions.ExplainAsync(await Park(stub), Ct);

        Assert.NotNull(explanation);
        Assert.Equal(SessionState.AwaitingApproval, explanation.Outcome);
        Assert.Equal([2, 3, 4, 8], explanation.Ladder.Select(s => s.Tier).Where(t => t is not null));
        Assert.Contains(explanation.Ladder, s => s.Tier is null);
    }

    [Fact]
    public async Task A_reset_clears_the_sessions_and_keeps_the_citizens()
    {
        using var stub = Manual();

        await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Kept", Id = "kept", DateOfBirth = new DateOnly(1985, 3, 29) }, Ct);
        await Park(stub);

        await stub.ResetAsync(Ct);

        Assert.Empty(await stub.Sessions.ListAsync(ct: Ct));
        Assert.Contains(await stub.Citizens.ListAsync(Ct), c => c.Id == "kept");
    }

    /// <remarks>
    /// Two suites against one instance would otherwise take each other's queued outcomes, and the
    /// loser's failure would name a login it never made.
    /// </remarks>
    [Fact]
    public async Task A_decision_queued_against_one_client_is_not_taken_by_another()
    {
        using var stub = Manual();

        await stub.Behaviour.EnqueueAsync(Decision.Refused().ForClient("somebody-else"), Ct);

        var session = await Park(stub);

        Assert.Equal(SessionState.AwaitingApproval, (await stub.Sessions.FindAsync(session, Ct))?.State);
    }

    [Fact]
    public async Task The_fidelity_ledger_round_trips_through_the_client()
    {
        using var stub = Connect();

        var entries = await stub.FidelityAsync(Ct);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Subject)));

        // Text, not a flag: it names the recording that is still missing.
        Assert.All(entries, e => Assert.True(e.AwaitingCapture is null or { Length: > 0 }));
    }

    [Fact]
    public async Task A_login_that_was_never_made_is_absent_rather_than_an_error()
    {
        using var stub = Connect();

        Assert.Null(await stub.Sessions.FindAsync("no-such-login", Ct));
        Assert.Null(await stub.Sessions.ExplainAsync("no-such-login", Ct));
        Assert.False(await stub.Citizens.DeleteAsync("no-such-citizen", Ct));
    }

    /// <summary>Drives a login as far as the parked state, and answers with its id.</summary>
    /// <remarks>
    /// The browser is a client of the same in-memory host. A plain HttpClient carrying the same
    /// base address would open a socket to localhost and wait for a server that is not there.
    /// </remarks>
    private async Task<string> Park(StubIdClient stub)
    {
        using var browser = _manual.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + "&redirect_uri=http://localhost:5099/callback"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var sessions = await stub.Sessions.ListAsync(SessionState.AwaitingApproval, ct: Ct);

        return sessions[0].Id;
    }

    /// <summary>
    /// Starts a login and returns it whatever became of it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Park" />, which waits for one that is still undecided. A queued
    /// decision resolves a login before the authorize response is written, so there is nothing
    /// awaiting approval to find - which is the whole point of the tier.
    /// </remarks>
    private async Task<string> Drive(StubIdClient stub, WebApplicationFactory<Program>? host = null)
    {
        // The host has to be the one the client is talking to. They are two separate instances,
        // and driving a login into one while asking the other about it finds nothing.
        using var browser = (host ?? _manual).CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + "&redirect_uri=http://localhost:5099/callback"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        // Newest first, which is the order the control API lists them in.
        var sessions = await stub.Sessions.ListAsync(ct: Ct);

        return sessions[0].Id;
    }
}
