using Microsoft.Extensions.Time.Testing;
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
        var session = store.Park("client", "?a=b", Context());

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
        var session = store.Park("client", "?a=b", Context());

        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "an operator"));
        clock.Advance(TimeSpan.FromMinutes(10));

        // The operator who clicked at the last moment gets their approval, rather than losing
        // it to a sweep that ran afterwards.
        Assert.Equal(SessionState.Approved, store.Find(session.Id)!.State);
    }

    [Fact]
    public void Approving_and_expiring_at_once_produces_one_outcome_not_a_coin_toss()
    {
        // A person clicking approve as the timeout fires is the ordinary case. Without a
        // single-writer rule the two race and the result depends on the machine.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var (store, clock, people) = Build(automatic: false);
            var session = store.Park("client", "?a=b", Context());
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
        var session = store.Park("client", "?a=b", Context());

        Assert.True(store.Decide(session.Id, Decision.Approved(people.Default!.Id), "first"));
        Assert.False(store.Decide(session.Id, Decision.Refused("mitid_user_aborted"), "second"));

        Assert.Equal(SessionState.Approved, session.State);
    }

    [Fact]
    public void A_code_is_collected_once()
    {
        var (store, _, people) = Build(automatic: true);
        var session = store.Park("client", "?a=b", Context());

        Assert.Equal(SessionState.Approved, session.State);
        Assert.True(session.TryRedeem());
        Assert.False(session.TryRedeem());
        Assert.Equal(SessionState.Redeemed, session.State);
    }

    [Fact]
    public void A_refused_login_cannot_be_redeemed()
    {
        var (store, _, _) = Build(automatic: false);
        var session = store.Park("client", "?a=b", Context());

        store.Decide(session.Id, Decision.Refused("mitid_user_aborted"), "an operator");

        Assert.False(session.TryRedeem());
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public void The_raw_query_is_kept_verbatim()
    {
        // A nonce is echoed byte for byte, and rebuilding a query from parsed parts is where
        // that quietly stops being true.
        const string query = "?client_id=x&nonce=637.abcDEF%2B%2F&state=a%20b";
        var (store, _, _) = Build(automatic: true);

        Assert.Equal(query, store.Park("client", query, Context()).RawQuery);
    }
    [Fact]
    public void A_decision_aimed_at_one_login_is_recorded_as_the_tier_it_is()
    {
        // Tier one arrives from outside the ladder, so the explanation has to say so - a
        // reader comparing precedence needs the decision and the ladder in one list.
        var clock = new FakeTimeProvider();
        var citizens = new Citizens();
        var store = new SessionStore(clock, new Ladder([new DefaultOutcome(citizens, () => false)]));

        var session = store.Park("client", "?a=b", Context());

        Assert.True(store.Decide(session.Id, Decision.Approved("default"), "the control API"));

        var decided = session.Explanation.Single(s => s.Outcome == "decided");

        Assert.Equal(1, decided.Tier);
        Assert.Equal("the control API", decided.Name);
    }

}
