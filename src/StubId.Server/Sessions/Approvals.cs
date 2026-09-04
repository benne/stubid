namespace StubId.Server.Sessions;

/// <summary>How deciding one login can go.</summary>
internal enum ApprovalResult
{
    /// <summary>This caller's decision is the one that took.</summary>
    Decided,

    /// <summary>The named citizen is not on this instance, so nothing was decided.</summary>
    NoSuchCitizen,

    /// <summary>Something else got there first. What happened is on the session.</summary>
    AlreadyDecided,

    /// <summary>No login by that id.</summary>
    NoSuchSession,
}

/// <param name="Result">Which of the four happened.</param>
/// <param name="CitizenId">Who the login was approved as, when it was approved.</param>
/// <param name="Session">
/// The login as it now stands, which is what a caller that lost the race needs: not an error, but
/// the outcome that actually happened. Null only when there is no such login.
/// </param>
internal readonly record struct ApprovalOutcome(
    ApprovalResult Result, string? CitizenId, AuthSession? Session);

/// <summary>What deciding one login means, wherever the decision came from.</summary>
/// <remarks>
/// There are three doors onto this now - the control API, the broker's own login page, and the
/// admin pages - and <c>docs/guides/approvals.md</c> promises they are one code path rather than
/// "two implementations that agree until one of them changes". They were two near-identical blocks
/// until a third caller made the promise worth keeping properly.
/// <para>
/// The <c>by</c> string is what the ladder records against the decision, so
/// <c>/sessions/{id}/explain</c> says which door it came through.
/// </para>
/// </remarks>
internal static class Approvals
{
    public static ApprovalOutcome Approve(
        SessionStore sessions, Citizens citizens, string id, string? citizenId, string by)
    {
        var citizen = citizenId is { Length: > 0 } named ? citizens.ById(named) : citizens.Default;

        if (citizen is null)
        {
            return new ApprovalOutcome(ApprovalResult.NoSuchCitizen, null, sessions.Find(id));
        }

        // The citizen's own rule applies here too. "Sign in as this person" has to mean the same
        // thing whether a test said it, an operator clicked it, or an admin page posted it.
        return sessions.Decide(id, citizen.Outcome(), by)
            ? new ApprovalOutcome(ApprovalResult.Decided, citizen.Id, sessions.Find(id))
            : Lost(sessions, id);
    }

    public static ApprovalOutcome Reject(
        SessionStore sessions, string id, string? errorCode, string? oauthError, string by) =>
        sessions.Decide(
            id,
            Decision.Refused(errorCode ?? "mitid_user_aborted", oauthError ?? "access_denied"),
            by)
            ? new ApprovalOutcome(ApprovalResult.Decided, null, sessions.Find(id))
            : Lost(sessions, id);

    // Decide returns false for both "already decided" and "no such login", and the two owe the
    // caller different answers.
    private static ApprovalOutcome Lost(SessionStore sessions, string id) =>
        sessions.Find(id) is { } session
            ? new ApprovalOutcome(ApprovalResult.AlreadyDecided, session.CitizenId, session)
            : new ApprovalOutcome(ApprovalResult.NoSuchSession, null, null);
}
