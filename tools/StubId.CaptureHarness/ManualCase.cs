namespace StubId.CaptureHarness;

/// <summary>Which set of credentials a step uses.</summary>
public enum ClientProfile
{
    /// <summary>The private client, which carries the richer scopes.</summary>
    Private,

    /// <summary>The broker's published code-flow client.</summary>
    OpenCode,

    /// <summary>The broker's published implicit client, for a front-channel id_token.</summary>
    OpenImplicit,

    /// <summary>
    /// A client whose redirect URIs are actually registered, unlike the published ones which
    /// accept anything. The only way to record how the broker refuses one it does not know.
    /// </summary>
    Restricted,

    /// <summary>First of a pair joined to the same service provider's single sign-on.</summary>
    SsoA,

    /// <summary>
    /// The second of that pair. Reaching it without a prompt is what single sign-on means, and
    /// what the recording is for.
    /// </summary>
    SsoB,

    /// <summary>
    /// Configured for the hybrid grant, which is the only way to observe c_hash. ASP.NET Core
    /// requires it whenever an id_token arrives through the front channel.
    /// </summary>
    Hybrid,
}

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
/// Deliberately separate from the unattended catalogue. Both <c>capture</c> and
/// <c>verify</c> iterate that one, and a routine run after the sitting would replay expired
/// codes and dead tokens over the evidence, then rehash the manifest across the damage.
/// </remarks>
public sealed class ManualCase
{
    public required string Id { get; init; }

    /// <summary>The step number in docs/capture-session.md, so the two stay aligned.</summary>
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

    public ClientProfile Client { get; init; } = ClientProfile.Private;

    public string Scope { get; init; } = "openid mitid";

    public string ResponseType { get; init; } = "code";

    public string? ResponseMode { get; init; }

    /// <summary>Broker parameters beyond the standard ones: idp_params, prompt, language.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
