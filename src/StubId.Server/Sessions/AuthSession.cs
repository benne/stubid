namespace StubId.Server.Sessions;

/// <summary>Where a login has got to.</summary>
public enum SessionState
{
    /// <summary>Validated and parked, waiting for something to decide it.</summary>
    AwaitingApproval,

    /// <summary>Decided in the caller's favour. A code has not been collected yet.</summary>
    Approved,

    /// <summary>The code was collected. Terminal.</summary>
    Redeemed,

    /// <summary>Refused, carrying the broker's own error code. Terminal.</summary>
    Failed,

    /// <summary>Nobody decided it in time. Terminal.</summary>
    Expired,
}

/// <summary>What a decision does to a session.</summary>
/// <param name="Approve">False means the session fails.</param>
/// <param name="ErrorCode">
/// The broker's own code, not a description. A client sees this in error_description, and a
/// test asserting on <c>mitid_user_aborted</c> needs the exact string.
/// </param>
/// <param name="Delay">
/// Held as data rather than slept through, so a decider stays pure and the scheduler decides
/// when it lands. A decider that slept would pin a thread and could not be replayed.
/// </param>
public sealed record Decision(
    bool Approve,
    string? CitizenId = null,
    string? ErrorCode = null,
    string? OAuthError = null,
    TimeSpan? Delay = null)
{
    /// <summary>
    /// True when the tier named who logs in without saying whether it succeeds, so the ladder
    /// carries the choice down to the tier that decides rather than short-circuiting.
    /// </summary>
    public bool Selects { get; init; }

    public static Decision Approved(string citizenId) => new(true, citizenId);

    public static Decision Selecting(string citizenId) => new(true, citizenId) { Selects = true };

    public static Decision Refused(string errorCode, string oauthError = "access_denied") =>
        new(false, ErrorCode: errorCode, OAuthError: oauthError);
}

/// <summary>
/// One login, from the moment it was validated to whatever became of it.
/// </summary>
/// <remarks>
/// Terminal states are written once. A human clicking approve as the timeout fires is the
/// ordinary case, not an edge case, and without a single-writer rule the two race and the
/// result depends on the machine. The loser of that race is told what actually happened
/// rather than being silently overwritten, which is what makes a test deterministic.
/// </remarks>
public sealed class AuthSession
{
    private readonly Lock _gate = new();

    public required string Id { get; init; }

    public required string ClientId { get; init; }

    /// <summary>The request this login is for, as it was parsed.</summary>
    /// <remarks>
    /// <para>
    /// The whole record rather than the query it arrived on, because the query cannot answer for
    /// it. There are four arrival shapes and the query is enough for two: a plain GET and a
    /// signed request object both carry everything, a form POST leaves it empty because the
    /// parameters were in the body, and a pushed request leaves a client id and a reference that
    /// has already been redeemed and cannot be redeemed twice.
    /// </para>
    /// <para>
    /// So the value is taken where the request has been parsed and every path has converged,
    /// which is the same rule the transaction text was carried by before this record subsumed
    /// it. Resuming a login needs the redirect URI, the response type and mode, the state and
    /// the PKCE challenge, and every one of them is in here on every shape.
    /// </para>
    /// </remarks>
    public required AuthorizationRequest Request { get; init; }

    /// <summary>The transaction text the request carried, base64 as it was sent, or null.</summary>
    /// <remarks>
    /// Stored as it arrived and decoded where it is rendered. Keeping the decoded string on a
    /// long-lived session would put a client-controlled string into everything that describes
    /// one, for the sake of a decode that costs nothing to repeat.
    /// </remarks>
    public string? TransactionText => Request.TransactionText;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset Deadline { get; init; }

    public SessionState State { get; private set; } = SessionState.AwaitingApproval;

    /// <summary>Guards the transition. The loser of a race sees a version it does not expect.</summary>
    public int Version { get; private set; }

    public string? CitizenId { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? OAuthError { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>How the decision was reached, for the explain endpoint.</summary>
    public IReadOnlyList<LadderStep> Explanation { get; internal set; } = [];

    public bool IsTerminal => State is SessionState.Redeemed or SessionState.Failed or SessionState.Expired;

    public bool IsDecided => State != SessionState.AwaitingApproval;

    /// <summary>
    /// Applies a decision, once. Returns false if something already decided this session,
    /// which is the answer a racing caller needs rather than an exception.
    /// </summary>
    public bool TryDecide(Decision decision, DateTimeOffset now, IReadOnlyList<LadderStep> explanation)
    {
        lock (_gate)
        {
            if (IsDecided)
            {
                return false;
            }

            State = decision.Approve ? SessionState.Approved : SessionState.Failed;
            CitizenId = decision.CitizenId;
            ErrorCode = decision.ErrorCode;
            OAuthError = decision.OAuthError;
            DecidedAt = now;
            Explanation = explanation;
            Version++;

            return true;
        }
    }

    /// <summary>Marks the code collected. Only an approved session can be redeemed, once.</summary>
    public bool TryRedeem()
    {
        lock (_gate)
        {
            if (State != SessionState.Approved)
            {
                return false;
            }

            State = SessionState.Redeemed;
            Version++;

            return true;
        }
    }

    /// <summary>
    /// Expires an undecided session. Loses to a decision that got there first, which is the
    /// whole point: the operator who clicked approve at the last moment gets their approval.
    /// </summary>
    public bool TryExpire(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (IsDecided)
            {
                return false;
            }

            State = SessionState.Expired;
            ErrorCode = "mitid_timeout";
            OAuthError = "access_denied";
            DecidedAt = now;
            Version++;

            return true;
        }
    }

    /// <summary>
    /// Expires a session that was approved and never collected. A second window, and it has to
    /// be a second one.
    /// </summary>
    /// <remarks>
    /// Approving stops the first deadline mattering - that is what
    /// <see cref="TryExpire(DateTimeOffset)"/> guarding on <see cref="IsDecided"/> is for, so
    /// that the operator who clicks at the last moment gets their approval. But until a login
    /// could be resumed, nothing ever collected an approval, and an approved session simply
    /// stayed in the store for the life of the instance. Measuring this window from the decision
    /// keeps both true: the last-moment approval still wins, and a day-old one does not still
    /// hand out a code.
    /// </remarks>
    public bool TryExpireUncollected(DateTimeOffset now, TimeSpan window)
    {
        lock (_gate)
        {
            if (State != SessionState.Approved || DecidedAt is not { } decided
                || now < decided + window)
            {
                return false;
            }

            State = SessionState.Expired;
            ErrorCode = "mitid_timeout";
            OAuthError = "access_denied";
            Version++;

            return true;
        }
    }
}
