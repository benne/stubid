using System.Collections.Concurrent;

namespace StubId.Server.Sessions;

/// <summary>
/// The parked logins, and the clock that expires them.
/// </summary>
/// <remarks>
/// Expiry is checked when a session is read rather than swept by a background timer. That
/// keeps it honest under a controllable clock: a test that moves time forward sees the effect
/// on its next request, with no sleeping and nothing to wait for.
/// </remarks>
public sealed class SessionStore(TimeProvider clock, Ladder ladder)
{
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a login may sit undecided. The broker's own timeout, so a test that waits it
    /// out sees the code the broker sends.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<AuthSession> All
    {
        get
        {
            foreach (var session in _sessions.Values)
            {
                Refresh(session);
            }

            return [.. _sessions.Values];
        }
    }

    /// <summary>The logins a filter names, newest first.</summary>
    /// <remarks>
    /// The control API and the admin pages answer to the same two filters, and held a copy of this
    /// each until the second one existed. Reading the list runs the expiry sweep, which is what
    /// makes an open page keep its own table honest.
    /// </remarks>
    public IReadOnlyList<AuthSession> Matching(string? state, string? clientId) =>
    [
        .. All
            .Where(s => state is null
                || s.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase))
            .Where(s => clientId is null || s.ClientId == clientId)
            .OrderByDescending(s => s.CreatedAt),
    ];

    public AuthSession Park(AuthorizationRequest request, SessionContext context)
    {
        var now = clock.GetUtcNow();
        var session = new AuthSession
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = request.ClientId,
            Request = request,
            CreatedAt = now,
            Deadline = now + Timeout,
        };

        _sessions[session.Id] = session;

        // Run the ladder immediately: most logins are decided by something already in place,
        // and parking one that a rule would have decided instantly would make every test wait
        // for nothing.
        var (decision, explanation) = ladder.Resolve(context with { SessionId = session.Id });

        if (decision is not null)
        {
            session.TryDecide(decision, now, explanation);
        }
        else
        {
            session.Explanation = explanation;
        }

        return session;
    }

    public AuthSession? Find(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        Refresh(session);
        return session;
    }

    /// <summary>
    /// Decides a parked session from outside - the control API, or someone clicking. Returns
    /// false when something already decided it, which is the answer a racing caller needs.
    /// </summary>
    public bool Decide(string id, Decision decision, string by)
    {
        var session = Find(id);

        return session is not null && session.TryDecide(
            decision,
            clock.GetUtcNow(),
            [.. session.Explanation, new LadderStep(1, by, "decided",
                decision.Approve
                    ? $"approved as {decision.CitizenId}"
                    : $"refused with {decision.ErrorCode}")]);
    }

    public void Clear() => _sessions.Clear();

    /// <summary>Expires a session whose deadline has passed, if nothing decided it first.</summary>
    private void Refresh(AuthSession session)
    {
        var now = clock.GetUtcNow();

        if (!session.IsDecided && now >= session.Deadline)
        {
            session.TryExpire(now);
            return;
        }

        // The second window. An approval nobody collects used to sit here for the life of the
        // instance, because expiry only ever looked at undecided sessions - which was invisible
        // while nothing could collect one.
        session.TryExpireUncollected(now, Timeout);
    }
}
