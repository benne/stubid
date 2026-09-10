namespace StubId.Client;

/// <summary>Where a login ended up. Terminal states are written once.</summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    /// <summary>Validated and parked, waiting for something to decide it.</summary>
    AwaitingApproval,

    /// <summary>Decided in the caller's favor. The code has not been collected yet.</summary>
    Approved,

    /// <summary>The code was collected. Terminal.</summary>
    Redeemed,

    /// <summary>Refused, carrying the broker's own error code. Terminal.</summary>
    Failed,

    /// <summary>
    /// Nothing decided it before its deadline, or it was approved and the code was never
    /// collected inside a second window of the same length. Terminal.
    /// </summary>
    Expired,
}

/// <summary>One login, as the control API describes it.</summary>
/// <param name="OauthError">
/// The other half of what a refusal sends. The broker puts its own code in
/// <c>error_description</c> and this in <c>error</c>, and a client that logged only one of them
/// was throwing away the half that says which kind of failure it was.
/// </param>
/// <param name="Version">
/// Moves once per decision, so two reads of the same login can be told apart.
/// </param>
public sealed record StubIdSession(
    string Id,
    string ClientId,
    SessionState State,
    string? CitizenId,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset Deadline,
    DateTimeOffset? DecidedAt,
    string? OauthError = null,
    int Version = 0);

/// <summary>One decision waiting to be taken by the next matching login.</summary>
/// <param name="ClientId">The client it is queued for, or <c>*</c> for any.</param>
/// <param name="Position">Where it sits in that client's queue, counting from one.</param>
public sealed record QueuedDecision(
    string ClientId,
    int Position,
    bool Approve,
    string? CitizenId,
    string? ErrorCode,
    string? Error);

/// <summary>
/// One thing the instance has handed out, described without handing it out again.
/// </summary>
/// <remarks>
/// There is no value here and no prefix of one. A code and an access token are credentials, and
/// the surface that serves this asks nobody who they are. <see cref="LoginId" /> is what lines an
/// entry up against a login, and it is null for a pushed request, which exists before there is one.
/// </remarks>
public sealed record IssuedArtifact(
    string Kind,
    string ClientId,
    string? CitizenId,
    string? LoginId,
    DateTimeOffset? AuthenticatedAt,
    DateTimeOffset? Expires,
    string? Scope);

/// <summary>One of the clients this broker publishes.</summary>
public sealed record RegisteredClient(
    string ClientId,
    IReadOnlyList<string> ResponseTypes,
    string Organization);

/// <summary>One route this build answers on.</summary>
/// <param name="Role">
/// What the route is for, so a caller can find one without knowing its path. Roles a single broker
/// invents for itself are prefixed <c>extra:</c>.
/// </param>
public sealed record EmulatedRoute(
    string Pattern,
    IReadOnlyList<string> Methods,
    string? Role)
{
    /// <summary>
    /// Whether this build answers the route or only admits that it does not.
    /// </summary>
    /// <remarks>
    /// The discovery document is served from a recording, so it advertises everything the broker
    /// does, and a few of those are not reproduced. Those routes are declared anyway - answering
    /// 501 with a link to the reason says something a 404 cannot - and this is how a caller tells
    /// them apart without reading the ledger. Not a positional member, so a suite that
    /// deserialized the old payload still compiles.
    /// </remarks>
    public bool Emulated { get; init; } = true;
}

/// <summary>
/// Whether logins decide themselves, and where that answer came from.
/// </summary>
/// <param name="Configured">What the instance was started with.</param>
/// <param name="Overridden">What was set while it ran, or null if nothing was.</param>
public sealed record StubIdApproval(bool Enabled, bool Configured, bool? Overridden);

/// <summary>What the instance's clock reads, and whether a test may move it.</summary>
public sealed record StubIdClock(DateTimeOffset Now, bool Controllable);

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
    /// <summary>
    /// Whether the login succeeds. <see cref="Approved" /> and <see cref="Refused" /> set it
    /// along with the fields that outcome needs.
    /// </summary>
    public required bool Approve { get; init; }

    /// <summary>Which client's logins this is for. Null takes the next one from any client.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Who an approval authenticates as, rule or no rule: a queued decision names an outcome
    /// rather than a person, which makes it the way to approve a rule-bearing citizen anyway.
    /// Null leaves the instance to pick, which is the citizen registered as the default one.
    /// </summary>
    public string? CitizenId { get; init; }

    /// <summary>
    /// The broker's own code a refusal fails with, which a client reads from
    /// <c>error_description</c>.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// The other half of a refusal, sent as <c>error</c>: which kind of failure it was.
    /// </summary>
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

    /// <summary>
    /// Who the login was resolved as, including when that person's own rule turned the approval
    /// into a failure. Null when the login was rejected outright, and after a lost race, where
    /// <see cref="Outcome" /> carries the winner's.
    /// </summary>
    public string? CitizenId { get; init; }

    /// <summary>Why not, when <see cref="Decided" /> is false.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// What actually happened, when <see cref="Decided" /> is false. This is the point of losing
    /// the race: both writers learn the same answer.
    /// </summary>
    public StubIdSession? Outcome { get; init; }
}

/// <summary>Which broker an instance emulates, and which recording of it.</summary>
/// <remarks>
/// Not the version of the package or the image, which move on every release. This one moves only
/// when a release carries a new capture, and it is the version a suite is pinning when it asserts
/// on bytes StubID reproduces rather than composes.
/// </remarks>
public sealed record StubIdProfile(string Broker, string Version)
{
    /// <summary>
    /// The path this broker's surface sits under, relative and without slashes at either end, or
    /// null when the instance is too old to say.
    /// </summary>
    /// <remarks>
    /// Appended to the address to form the authority a client library is configured with, which
    /// the issuer it then discovers equals character for character. Nullable because the empty
    /// string is a real answer - it is what a broker served at the host root reports - so a
    /// missing field and a broker with no path of its own must not arrive here as the same value.
    /// An instance that predates the field was serving the first broker, which is the only one
    /// there was.
    /// </remarks>
    public string? Root { get; init; }

    /// <summary>The form the ledger and the release notes write it in.</summary>
    public override string ToString() => $"{Broker}@{Version}";
}

/// <summary>One annotated piece of emulated behavior, as the running instance reports it.</summary>
public sealed record FidelityEntry(
    string Subject,
    string Tier,
    string Provenance,
    string? Evidence,
    string? Reason,
    string? AwaitingCapture,
    bool Complete);
