using Microsoft.Extensions.Time.Testing;
using StubId.Server;
using StubId.Server.Sessions;

namespace StubId.Sessions.Tests;

/// <summary>
/// The two properties that make a test of a login deterministic.
/// </summary>
public class SessionStateTests
{
    private static SessionContext Context(string sessionId = "s") =>
        new(sessionId, "client", "openid mitid",
            new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The request a parked login is for. The session carries the whole record, because the
    /// query it arrived on cannot answer for it on two of the four arrival shapes.
    /// </summary>
    private static AuthorizationRequest Request() =>
        new("client", "http://localhost:5099/callback", "code", "query", "openid mitid",
            State: "s", Nonce: "n", CodeChallenge: null, CodeChallengeMethod: null);

    private static (SessionStore Store, FakeTimeProvider Clock, Citizens People) Build(bool automatic)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var people = new Citizens();
        var ladder = new Ladder([new DefaultOutcome(people, () => automatic)]);

        return (new SessionStore(clock, ladder), clock, people);
    }

    [Fact]
    public void A_five_minute_timeout_is_tested_in_milliseconds()
    {
        // The reason the clock is injected. A test that waited five minutes would be deleted;
        // one that cannot reach the timeout at all never gets written.
        var (store, clock, _) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        Assert.Equal(SessionState.AwaitingApproval, session.State);

        clock.Advance(TimeSpan.FromMinutes(5));

        var after = store.Find(session.Id)!;

        Assert.Equal(SessionState.Expired, after.State);
        Assert.Equal("mitid_timeout", after.ErrorCode);
    }

    [Fact]
    public void A_decision_that_arrives_before_the_deadline_survives_it()
    {
        var (store, clock, people) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        // Approved with four of the five minutes already gone.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "an operator"));

        // The operator who clicked at the last moment gets their approval, rather than losing
        // it to a sweep that ran afterwards. Past the parked deadline, and still approved.
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(SessionState.Approved, store.Find(session.Id)!.State);
    }

    [Fact]
    public void An_approval_nobody_collects_expires_on_a_window_of_its_own()
    {
        // The second deadline, and it has to be a second one. Approving stops the first
        // mattering, so before a login could be resumed an approved session stayed in the store
        // for the life of the instance - which was invisible while nothing ever collected one.
        var (store, clock, people) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "an operator"));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(SessionState.Approved, store.Find(session.Id)!.State);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(SessionState.Expired, store.Find(session.Id)!.State);
        Assert.Equal("mitid_timeout", store.Find(session.Id)!.ErrorCode);
    }

    [Fact]
    public void A_collected_login_is_not_expired_afterwards()
    {
        // Redeemed is terminal, and the collection window must not reopen it: a client holding
        // a code would otherwise see its login turn into a timeout behind it.
        var (store, clock, people) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "an operator"));
        Assert.True(session.TryRedeem());

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(SessionState.Redeemed, store.Find(session.Id)!.State);
    }

    [Fact]
    public void Approving_and_expiring_at_once_produces_one_outcome_not_a_coin_toss()
    {
        // A person clicking approve as the timeout fires is the ordinary case. Without a
        // single-writer rule the two race and the result depends on the machine.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var (store, clock, people) = Build(automatic: false);
            var session = store.Park(Request(), Context());
            clock.Advance(TimeSpan.FromMinutes(5));

            var approved = false;
            var expired = false;

            Parallel.Invoke(
                () => approved = session.TryDecide(
                    Decision.Approved(people.Default!.Id), clock.GetUtcNow(), []),
                () => expired = session.TryExpire(clock.GetUtcNow()));

            Assert.True(approved ^ expired, "exactly one of the two writers must win");
            Assert.Equal(
                approved ? SessionState.Approved : SessionState.Expired,
                session.State);
        }
    }

    [Fact]
    public void The_loser_of_a_race_is_told_rather_than_silently_overwritten()
    {
        var (store, _, people) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "first"));
        Assert.False(store.Decide(session.Id, Decision.Refused("mitid_user_aborted"), "second"));

        Assert.Equal(SessionState.Approved, session.State);
    }

    [Fact]
    public void A_code_is_collected_once()
    {
        var (store, _, people) = Build(automatic: true);
        var session = store.Park(Request(), Context());

        Assert.Equal(SessionState.Approved, session.State);
        Assert.True(session.TryRedeem());
        Assert.False(session.TryRedeem());
        Assert.Equal(SessionState.Redeemed, session.State);
    }

    [Fact]
    public void A_refused_login_cannot_be_redeemed()
    {
        var (store, _, _) = Build(automatic: false);
        var session = store.Park(Request(), Context());

        store.Decide(session.Id, Decision.Refused("mitid_user_aborted"), "an operator");

        Assert.False(session.TryRedeem());
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public void The_request_is_kept_whole()
    {
        // Not the query it arrived on. A form POST leaves that empty and a pushed request
        // leaves a reference already redeemed, so a login resumed from the query would work on
        // a plain GET and lose the other two arrival shapes with nothing to say why.
        var (store, _, _) = Build(automatic: true);
        var request = Request();

        Assert.Same(request, store.Park(request, Context()).Request);
    }
    [Fact]
    public void A_decision_aimed_at_one_login_is_recorded_as_the_tier_it_is()
    {
        // Tier one arrives from outside the ladder, so the explanation has to say so - a
        // reader comparing precedence needs the decision and the ladder in one list.
        var clock = new FakeTimeProvider();
        var citizens = new Citizens();
        var store = new SessionStore(clock, new Ladder([new DefaultOutcome(citizens, () => false)]));

        var session = store.Park(Request(), Context());

        Assert.True(store.Decide(session.Id, Decision.Approved("default"), "the control API"));

        var decided = session.Explanation.Single(s => s.Outcome == "decided");

        Assert.Equal(1, decided.Tier);
        Assert.Equal("the control API", decided.Name);
    }

}
