namespace StubId.Client;

/// <summary>Where a login ended up. Terminal states are written once.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    AwaitingApproval,
    Approved,
    Redeemed,
    Failed,
    Expired,
}

/// <summary>One login, as the control API describes it.</summary>
public sealed record StubIdSession(
    string Id,
    string ClientId,
    SessionState State,
    string? CitizenId,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset Deadline,
    DateTimeOffset? DecidedAt);

/// <summary>One tier of the resolution ladder, as it was applied or skipped.</summary>
/// <param name="Tier">Null for the step that had no tier of its own.</param>
public sealed record LadderStep(int? Tier, string Name, string Outcome, string Reason);

/// <summary>Why a login went the way it did, tier by tier, including the skipped ones.</summary>
public sealed record SessionExplanation(
    string Session,
    SessionState Outcome,
    IReadOnlyList<LadderStep> Ladder);

/// <summary>An outcome to apply to a login: who it is, or why it fails.</summary>
public sealed record Decision
{
    public required bool Approve { get; init; }

    /// <summary>Which client's logins this is for. Null takes the next one from any client.</summary>
    public string? ClientId { get; init; }

    public string? CitizenId { get; init; }

    public string? ErrorCode { get; init; }

    public string? Error { get; init; }

    /// <summary>Approves as the named citizen, or as the default one.</summary>
    public static Decision Approved(string? citizenId = null) =>
        new() { Approve = true, CitizenId = citizenId };

    /// <summary>
    /// Refuses with a broker error code. The defaults are what a user who aborted produces, which
    /// is the case worth reaching for first.
    /// </summary>
    public static Decision Refused(
        string errorCode = "mitid_user_aborted",
        string error = "access_denied") =>
        new() { Approve = false, ErrorCode = errorCode, Error = error };

    /// <summary>
    /// Scopes this to one client, so two suites queueing against one instance do not take each
    /// other's decisions.
    /// </summary>
    public Decision ForClient(string clientId) => this with { ClientId = clientId };
}

/// <summary>What a decision did, including losing the race, which is an outcome and not an error.</summary>
public sealed record DecisionOutcome
{
    /// <summary>False when something had already decided this login.</summary>
    public required bool Decided { get; init; }

    /// <summary>
    /// Where the login ended up. Null after a refusal, which the server does not report a state
    /// for.
    /// </summary>
    public SessionState? State { get; init; }

    public string? CitizenId { get; init; }

    /// <summary>Why not, when <see cref="Decided" /> is false.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// What actually happened, when <see cref="Decided" /> is false. This is the point of losing
    /// the race: both writers learn the same answer.
    /// </summary>
    public StubIdSession? Outcome { get; init; }
}

/// <summary>One annotated piece of emulated behaviour, as the running instance reports it.</summary>
public sealed record FidelityEntry(
    string Subject,
    string Tier,
    string Provenance,
    string? Evidence,
    string? Reason,
    string? AwaitingCapture,
    bool Complete);
