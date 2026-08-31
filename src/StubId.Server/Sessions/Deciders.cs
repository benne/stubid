using System.Collections.Concurrent;

namespace StubId.Server.Sessions;

// Tier 1 is a decision applied to one named login - the control API's approve and reject,
// and the login page's two buttons. It is not a decider, because it cannot be one: the ladder
// runs when a login parks, and nothing can name a session before it exists. So the tier is
// real and its number is used, but it arrives from outside and SessionStore records it.

/// <summary>
/// Tier 2: a one-shot decision queued in advance, consumed by the next matching login.
/// </summary>
/// <remarks>
/// The primary way a test drives this. A test that queues an outcome and then drives its
/// application needs no persistent rule, no cleanup, and no coordination with other tests
/// beyond using its own client.
/// </remarks>
public sealed class EnqueuedDecisions : ISessionDecider
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Decision>> _queues =
        new(StringComparer.Ordinal);

    public int Tier => 2;

    public string Name => "enqueued decision";

    /// <summary>Queued against a client, or against every client when none is named.</summary>
    public void Enqueue(Decision decision, string? clientId = null) =>
        _queues.GetOrAdd(clientId ?? "*", _ => new ConcurrentQueue<Decision>()).Enqueue(decision);

    /// <summary>
    /// Drops everything still queued. A decision left over from one test and consumed by the
    /// next is the contamination this whole tier is otherwise so useful for avoiding.
    /// </summary>
    public void Clear() => _queues.Clear();

    public Decision? Decide(SessionContext context, out string reason)
    {
        foreach (var key in new[] { context.ClientId, "*" })
        {
            if (_queues.TryGetValue(key, out var queue) && queue.TryDequeue(out var decision))
            {
                reason = key == "*"
                    ? "took the next decision queued for any client"
                    : $"took the next decision queued for {key}";
                return decision;
            }
        }

        reason = "nothing queued for this client";
        return null;
    }
}

/// <summary>
/// Tier 3: the broker's own simulation parameter, which names an identity in the request.
/// </summary>
/// <remarks>
/// Supported because it is the incumbent's published grammar: a team already paying for the
/// broker's simulation feature can point at StubID and change nothing else. The identity is
/// resolved the way the parameter documents it, by uuid, then username, then personal number.
/// </remarks>
public sealed class SimulationParameter(Citizens citizens) : ISessionDecider
{
    public int Tier => 3;

    public string Name => "simulation parameter";

    /// <summary>The two the broker's grammar defines. Anything else is not a mode.</summary>
    private static readonly string[] Modes = ["ui", "no-ui"];

    public Decision? Decide(SessionContext context, out string reason)
    {
        if (!context.Parameters.TryGetValue("simulation", out var raw))
        {
            reason = "the request carried no simulation parameter";
            return null;
        }

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // CAP-013: a mode the broker does not define is not an error. The request was accepted
        // and sent on to the authenticator, so the parameter is ignored rather than refused,
        // and the login proceeds as though it had not been sent.
        if (tokens.Length == 0 || !Modes.Contains(tokens[0], StringComparer.Ordinal))
        {
            reason = $"'{raw}' names no mode the broker defines, so the parameter is ignored";
            return null;
        }

        var directives = tokens.Skip(1)
            .Select(t => t.Split(':', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        // A mode on its own asks for a login without a person in it, not for a particular
        // person. Stepping aside here would park it at a page, which is the one thing the
        // parameter exists to avoid.
        if (directives.Count == 0)
        {
            if (citizens.Default is not { } fallback)
            {
                reason = "a simulated login was asked for, and there is nobody to simulate";
                return Decision.Refused("mitid_simulation_unknown_user");
            }

            reason = $"a simulated login naming nobody, so {fallback.Id}";
            return Decision.Selecting(fallback.Id);
        }

        var citizen = Resolve(directives);

        if (citizen is null)
        {
            reason = "the simulation parameter named nobody this instance knows";
            return Decision.Refused("mitid_simulation_unknown_user");
        }

        // Names who, not whether. What a login as that person does is the next tier's answer.
        reason = $"the simulation parameter named {citizen.Id}";
        return Decision.Selecting(citizen.Id);
    }

    private Citizen? Resolve(IReadOnlyDictionary<string, string> directives)
    {
        if (directives.TryGetValue("uuid", out var uuid))
        {
            return citizens.ByUuid(uuid);
        }

        if (directives.TryGetValue("username", out var username))
        {
            return citizens.ByUserName(username);
        }

        return directives.TryGetValue("cpr", out var cpr) ? citizens.ByCpr(cpr) : null;
    }
}

/// <summary>
/// Tier 4: what a login as the chosen person does.
/// </summary>
/// <remarks>
/// Only reachable once something above has chosen someone - the simulation parameter, today.
/// A rule here is how a suite gets "signing in as this person always aborts" without writing
/// per-test setup, which is the shape most failure tests actually want.
/// </remarks>
public sealed class CitizenRules(Citizens citizens) : ISessionDecider
{
    public int Tier => 4;

    public string Name => "the chosen citizen";

    public Decision? Decide(SessionContext context, out string reason)
    {
        if (context.SelectedCitizenId is not { } id)
        {
            reason = "nothing above chose an identity";
            return null;
        }

        if (citizens.ById(id) is not { } citizen)
        {
            reason = $"the chosen identity {id} no longer exists";
            return Decision.Refused("mitid_identity_not_found");
        }

        reason = citizen.Rule is null
            ? $"{citizen.Id} has no rule, so the login proceeds"
            : $"{citizen.Id} is set to fail with {citizen.Rule}";

        return citizen.Outcome();
    }
}

/// <summary>
/// Tier 8: what happens when nothing else had an opinion.
/// </summary>
/// <remarks>
/// Automatic where a test would otherwise hang, manual where a person is watching. Tiers five
/// to seven are deliberately absent: rules scoped to groups, clients and tenants triple the
/// precedence matrix and serve an audience this does not have yet. Their numbers are left free
/// so adding them later does not renumber the ladder.
/// </remarks>
public sealed class DefaultOutcome(Citizens citizens, Func<bool> approveAutomatically) : ISessionDecider
{
    public int Tier => 8;

    public string Name => "default";

    public Decision? Decide(SessionContext context, out string reason)
    {
        if (!approveAutomatically())
        {
            reason = "waiting for someone to decide, because this instance approves manually";
            return null;
        }

        var citizen = citizens.Default;

        if (citizen is null)
        {
            reason = "this instance approves automatically, but there are no citizens";
            return Decision.Refused("mitid_identity_not_found");
        }

        reason = citizen.Rule is null
            ? $"this instance approves automatically, as {citizen.Id}"
            : $"this instance authenticates as {citizen.Id}, who is set to fail with {citizen.Rule}";

        return citizen.Outcome();
    }
}
