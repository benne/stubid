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
}
