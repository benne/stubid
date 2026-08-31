using StubId.Server.Sessions;
using StubId.Wire;

namespace StubId.Sessions.Tests;

public class LadderTests
{
    private static SessionContext Context(
        string clientId = "client", IReadOnlyDictionary<string, string>? parameters = null) =>
        new("session", clientId, "openid mitid",
            parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch);

    private static Ladder Build(Citizens citizens, bool approveAutomatically = true) =>
        new([
            new EnqueuedDecisions(),
            new SimulationParameter(citizens),
            new CitizenRules(citizens),
            new DefaultOutcome(citizens, () => approveAutomatically),
        ]);

    [Fact]
    public void A_tier_with_no_opinion_steps_aside_rather_than_shadowing_the_rest()
    {
        var citizens = new Citizens();

        var (decision, explanation) = Build(citizens).Resolve(Context());

        Assert.NotNull(decision);
        Assert.True(decision.Approve);

        // Every tier appears, decided or skipped. Without that record, precedence is guesswork
        // the moment more than one rule could apply.
        // Every tier the ladder runs appears, decided or skipped. Tier one is absent because
        // it is not a decider: a decision aimed at one login can only arrive after that login
        // exists, which is after the ladder has run.
        Assert.Equal([2, 3, 4, 8], explanation.Select(s => s.Tier));
        Assert.Equal(["skipped", "skipped", "skipped", "decided"], explanation.Select(s => s.Outcome));
    }

    [Fact]
    public void An_enqueued_decision_is_consumed_once()
    {
        var citizens = new Citizens();
        var queue = new EnqueuedDecisions();
        var ladder = new Ladder([queue, new DefaultOutcome(citizens, () => true)]);

        queue.Enqueue(Decision.Refused("mitid_timeout"), "client");

        var first = ladder.Resolve(Context()).Decision;
        var second = ladder.Resolve(Context()).Decision;

        Assert.False(first!.Approve);
        Assert.True(second!.Approve);   // fell through to the default
    }

    [Fact]
    public void An_enqueued_decision_only_reaches_the_client_it_was_queued_for()
    {
        var citizens = new Citizens();
        var queue = new EnqueuedDecisions();
        var ladder = new Ladder([queue, new DefaultOutcome(citizens, () => true)]);

        queue.Enqueue(Decision.Refused("mitid_user_aborted"), "one-client");

        Assert.True(ladder.Resolve(Context("another-client")).Decision!.Approve);
        Assert.False(ladder.Resolve(Context("one-client")).Decision!.Approve);
    }

    [Fact]
    public void The_simulation_parameter_names_an_identity_the_way_the_broker_documents()
    {
        var citizens = new Citizens();
        var expected = citizens.Default!;
        var ladder = Build(citizens);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["simulation"] = $"no-ui uuid:{expected.Uuid}",
        };

        var (decision, explanation) = ladder.Resolve(Context(parameters: parameters));

        Assert.True(decision!.Approve);
        Assert.Equal(expected.Id, decision.CitizenId);

        // Naming someone says who logs in, not that the login succeeds. Tier three chooses
        // and steps aside; tier four is what settles the outcome.
        Assert.Equal("selected", explanation.Single(s => s.Tier == 3).Outcome);
        Assert.Equal("decided", explanation.Single(s => s.Tier == 4).Outcome);
    }

    [Fact]
    public void A_citizen_set_to_fail_fails_wherever_they_were_chosen()
    {
        // The shape a failure test wants: set the person up once, then write ordinary tests.
        var citizens = new Citizens();
        var aborts = citizens.Create(
            "aborts", "Test Person", new DateOnly(1990, 1, 1), Gender.Female, rule: "mitid_user_aborted");

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["simulation"] = $"no-ui uuid:{aborts.Uuid}",
        };

        var (decision, explanation) = Build(citizens).Resolve(Context(parameters: parameters));

        Assert.False(decision!.Approve);
        Assert.Equal("mitid_user_aborted", decision.ErrorCode);
        Assert.Equal(4, explanation.Single(s => s.Outcome == "decided").Tier);
    }

    [Fact]
    public void A_rule_on_the_default_citizen_holds_under_automatic_approval()
    {
        // Otherwise the rule would be honoured only on the paths that name someone, and the
        // most common path - approve whoever, automatically - would quietly ignore it.
        var citizens = new Citizens();
        citizens.Add(citizens.Default! with { Rule = "mitid_timeout" });

        var (decision, explanation) = Build(citizens).Resolve(Context());

        Assert.False(decision!.Approve);
        Assert.Equal("mitid_timeout", decision.ErrorCode);
        Assert.Equal(8, explanation.Single(s => s.Outcome == "decided").Tier);
    }

    [Fact]
    public void Nothing_above_chose_anyone_so_tier_four_steps_aside()
    {
        var (_, explanation) = Build(new Citizens()).Resolve(Context());

        var tierFour = explanation.Single(s => s.Tier == 4);

        Assert.Equal("skipped", tierFour.Outcome);
        Assert.Equal("nothing above chose an identity", tierFour.Reason);
    }

    [Fact]
    public void A_simulation_parameter_naming_nobody_is_refused_with_the_brokers_own_code()
    {
        var ladder = Build(new Citizens());
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["simulation"] = "no-ui uuid:11111111-2222-3333-4444-555555555555",
        };

        var (decision, _) = ladder.Resolve(Context(parameters: parameters));

        Assert.False(decision!.Approve);
        Assert.Equal("mitid_simulation_unknown_user", decision.ErrorCode);
    }

    [Fact]
    public void Nothing_decides_a_login_when_the_instance_approves_manually()
    {
        var (decision, explanation) = Build(new Citizens(), approveAutomatically: false).Resolve(Context());

        Assert.Null(decision);
        Assert.Equal("undecided", explanation[^1].Outcome);
    }
}
