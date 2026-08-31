namespace StubId.Server.Sessions;

/// <summary>What one tier of the ladder did, and why.</summary>
/// <param name="Tier">Its fixed number, so a skipped tier is still visible by its absence.</param>
/// <param name="Outcome">Decided, skipped, or had no opinion.</param>
public sealed record LadderStep(int Tier, string Name, string Outcome, string Reason);

/// <summary>A source of decisions, consulted in a fixed order.</summary>
/// <remarks>
/// Returning null means no opinion, which is how the tiers compose: a tier that does not apply
/// steps aside rather than shadowing the ones below it.
/// </remarks>
public interface ISessionDecider
{
    int Tier { get; }

    string Name { get; }

    /// <summary>
    /// Pure and synchronous on purpose. A decider that slept would pin a thread and could not
    /// be replayed; a delay is data on the decision, and the scheduler applies it.
    /// </summary>
    Decision? Decide(SessionContext context, out string reason);
}

/// <summary>What a decider is told about the login it is deciding.</summary>
public sealed record SessionContext(
    string SessionId,
    string ClientId,
    string Scope,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset Now)
{
    /// <summary>
    /// Who a higher tier chose, if one did. Selecting an identity and deciding an outcome are
    /// separate questions: naming a person in the request says who is logging in, not that the
    /// login succeeds, and a rule attached to that person is what settles the second half.
    /// </summary>
    public string? SelectedCitizenId { get; init; }
}

/// <summary>
/// Runs the tiers in order and records what each one did.
/// </summary>
/// <remarks>
/// The fixed order is the contract. Without a record of which tiers were skipped and why,
/// precedence is guesswork the moment more than one rule could apply — so the explanation is
/// built whether or not anyone asks for it, and served at
/// <c>/_stubid/v1/sessions/{id}/explain</c>.
/// </remarks>
public sealed class Ladder(IEnumerable<ISessionDecider> deciders)
{
    private readonly List<ISessionDecider> _deciders = [.. deciders.OrderBy(d => d.Tier)];

    public (Decision? Decision, IReadOnlyList<LadderStep> Explanation) Resolve(SessionContext context)
    {
        var steps = new List<LadderStep>();

        foreach (var decider in _deciders)
        {
            var decision = decider.Decide(context, out var reason);

            if (decision is null)
            {
                steps.Add(new LadderStep(decider.Tier, decider.Name, "skipped", reason));
                continue;
            }

            if (decision.Selects)
            {
                steps.Add(new LadderStep(decider.Tier, decider.Name, "selected", reason));
                context = context with { SelectedCitizenId = decision.CitizenId };
                continue;
            }

            steps.Add(new LadderStep(decider.Tier, decider.Name, "decided",
                $"{(decision.Approve ? "approve" : $"refuse with {decision.ErrorCode}")}: {reason}"));

            return (decision, steps);
        }

        steps.Add(new LadderStep(int.MaxValue, "nothing", "undecided",
            "no tier had an opinion, so the login waits"));

        return (null, steps);
    }
}
