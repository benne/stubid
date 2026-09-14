namespace StubId.CaptureHarness;

/// <summary>Work the harness does after the code has been exchanged, without the operator.</summary>
public enum FollowUp
{
    UserInfo,
    CprMatch,
    EndSession,

    /// <summary>Redeem the same code twice, to record what replay does.</summary>
    ReplayCode,
}

/// <summary>
/// One recording from the sitting: what to send, what the operator does, and what it settles.
/// </summary>
/// <remarks>
/// Deliberately separate from the unattended catalog. Both <c>capture</c> and
/// <c>verify</c> iterate that one, and a routine run after the sitting would replay expired
/// codes and dead tokens over the evidence, then rehash the manifest across the damage.
/// </remarks>
public sealed class ManualCase
{
    public required string Id { get; init; }

    /// <summary>
    /// The step heading in docs/capture-session.md, so the two stay aligned: the first broker's in
    /// Parts 3 and 6, the second's in Part 7.
    /// </summary>
    public required string Step { get; init; }

    public required string Title { get; init; }

    public required string Settles { get; init; }

    /// <summary>What the human does once the browser reaches MitID.</summary>
    public required string Operator { get; init; }

    /// <summary>
    /// Whether the step forces a fresh authentication.
    /// </summary>
    /// <remarks>
    /// A login leaves a broker session behind, and the next step rides it: the browser goes
    /// straight through without ever reaching the authenticator. For a step that needs the
    /// operator to approve something - to see a reference text in the app, or to type a CPR -
    /// that silently records the wrong thing while looking like a success. Those steps send
    /// prompt=login. The single sign-on step is the one that must not.
    /// </remarks>
    public bool ForcesLogin { get; init; } = true;

    /// <summary>
    /// The registration this step records with. Required, with no default: on the second broker
    /// a claim is a property of the client as much as of the broker, so a step that did not say
    /// which client it used would record something nobody could read back.
    /// </summary>
    public required BrokerClient Client { get; init; }

    /// <summary>What the step asks for.</summary>
    /// <remarks>
    /// Required, like the client. It used to default to the first broker's <c>openid mitid</c>, and
    /// <c>mitid</c> is not a scope on the second: a step that forgot to say would have been refused
    /// for asking.
    /// </remarks>
    public required string Scope { get; init; }

    public string ResponseType { get; init; } = "code";

    public string? ResponseMode { get; init; }

    /// <summary>Broker parameters beyond the standard ones: idp_params, prompt, language.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Sends the whole request as a signed JWT instead of as query parameters: HS256 over the client
    /// secret on the first broker, and with the registered key on the second.
    /// </summary>
    /// <remarks>
    /// The first broker limits the transaction-text flow to signed requests, so a step that wants
    /// the transaction-text claims has to set this. On the second, three of the four clients refuse
    /// anything else, and <see cref="BrokerClient.RequiresRequestObject" /> says which. The query
    /// then carries only client_id, response_type and request, and everything else - scope,
    /// redirect_uri, nonce, PKCE and the step's own Extra - travels inside the object. That the broker reads them from
    /// there was measured rather than assumed: docs/research/signed-requests.md.
    /// </remarks>
    public bool SignRequest { get; init; }

    /// <summary>False when the step is expected to end in a refusal rather than a code.</summary>
    public bool ExpectCode { get; init; } = true;

    public IReadOnlyList<FollowUp> FollowUps { get; init; } = [FollowUp.UserInfo];

    /// <summary>
    /// Sent instead of the harness's own callback, to record a refusal. The broker never
    /// redirects an invalid request back, so nothing arrives at the callback and the
    /// recording is the browser's landing page.
    /// </summary>
    public string? RedirectUriOverride { get; init; }
}
