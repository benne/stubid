namespace StubId.CaptureHarness;

/// <summary>
/// The sitting, in the order it has to happen.
/// </summary>
/// <remarks>
/// Order is not stylistic. A login establishes a broker session that changes what the next
/// step sees, the CPR match only works within fifteen minutes of its session, and end session
/// is terminal for the identity that ran it. The full reasoning, and what "this went wrong"
/// looks like at each step, is in docs/capture-session.md.
/// </remarks>
public static class ManualCatalogue
{
    private const string MitIdOnly = "mitid";

    /// <summary>Everything the private client is entitled to ask for.</summary>
    private const string FullScope =
        "openid mitid ssn nemid.pid ssn.details_name ssn.details_address userinfo_token transaction_token";

    /// <summary>
    /// In the order the sitting has to run, which is the step order rather than the case
    /// number. A login establishes a broker session, so the steps that record a refusal or an
    /// abort have to happen before the first successful one or they record something else.
    /// </summary>
    public static IReadOnlyList<ManualCase> All =>
    [
        new()
        {
            Id = "CAP-023",
            Step = "Step 2",
            Title = "Abort inside the MitID widget",
            Settles = "Which OAuth error accompanies a user abort, and whether the broker "
                + "redirects it back to the client or shows its own page.",
            Operator = "Start the login, then cancel inside the MitID widget.",
            ExpectCode = false,
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-028",
            Step = "Step 5",
            Title = "An unregistered redirect URI",
            Settles = "How the broker refuses a redirect URI it does not know. Every client "
                + "available until now accepted arbitrary ones, so this path has never been "
                + "observed, and StubID has to reproduce it.",
            Operator = "Nothing. The browser should land on the broker's error page and the "
                + "client should never be redirected back. Copy the error code the page shows.",
            Client = ClientProfile.Restricted,
            RedirectUriOverride = "http://localhost:5099/not-registered",
            ExpectCode = false,
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-020",
            Step = "Step 6",
            Title = "The baseline login",
            Settles = "The id_token member set and order, whether nbf and sid are really there, "
                + "the amr wire form, and the shape of a successful token response.",
            Operator = "Approve with the code app at its ordinary level.",
            FollowUps = [FollowUp.UserInfo, FollowUp.ReplayCode],
        },
        new()
        {
            Id = "CAP-021",
            Step = "Step 7",
            Title = "The full-scope login",
            Settles = "Every userinfo value and its JSON type, the address and name details, "
                + "the userinfo token, and the transaction token's base claim set.",
            Operator = "Approve, then type the identity's CPR when MitID asks for it.",
            Scope = FullScope,
            FollowUps = [FollowUp.UserInfo, FollowUp.CprMatch],
        },
        new()
        {
            Id = "CAP-022",
            Step = "Step 9",
            Title = "Transaction token with a reference text",
            Settles = "Which spelling the transaction token really uses for its text claims, "
                + "and whether it carries loa, aal, exp and aud at all.",
            Operator = "Check the reference text appears in the app, then approve.",
            Scope = "openid mitid transaction_token",
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["idp_params"] = """{"mitid":{"reference_text":"U3R1YklEIHJlZmVyZW5jZSB0ZXh0"}}""",
            },
        },
        new()
        {
            Id = "CAP-024",
            Step = "Step 11",
            Title = "Assurance level Low",
            Settles = "Whether loa, ial and aal move together, and which amr a lower level "
                + "produces.",
            Operator = "Approve with the lowest-friction authenticator offered.",
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["idp_params"] = """{"mitid":{"loa_value":"low"}}""",
            },
        },
        new()
        {
            Id = "CAP-025",
            Step = "Step 12",
            Title = "Single sign-on, second client",
            Settles = "Whether a second client rides the existing session without a prompt, "
                + "and whether the subject differs per client while mitid.uuid does not.",
            Operator = "Nothing: this should complete without asking anything. If it asks, "
                + "the session from the previous step was not reused and that is the finding.",
            Client = ClientProfile.OpenCode,
            ForcesLogin = false,
        },
        new()
        {
            Id = "CAP-026",
            Step = "Step 15",
            Title = "Front-channel id_token",
            Settles = "The form_post envelope, and c_hash, which ASP.NET Core requires "
                + "whenever an id_token arrives through the front channel.",
            Operator = "Approve as normal.",
            Client = ClientProfile.OpenImplicit,
            ResponseType = "id_token",
            ResponseMode = "form_post",
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-027",
            Step = "Step 16",
            Title = "End session",
            Settles = "What end session does with and without an id_token_hint, and whether "
                + "post_logout_redirect_uri is honoured either way.",
            Operator = "Follow the logout through to wherever it lands.",
            FollowUps = [FollowUp.EndSession],
        },
    ];
}
