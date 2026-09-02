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
            Id = "CAP-031",
            Step = "Step 9b",
            Title = "Transaction token with a transaction text, in a signed request",
            Settles = "How the transaction-text claims are really spelled - transaction_text "
                + "against transactiontext - and whether transaction_text_sha256 and "
                + "transaction_text_type are issued at all. The half of question 4 that "
                + "CAP-022 could not reach.",
            Operator = "Read the transaction text in the app and check it word for word "
                + "before approving. Whether it is displayed at all is the finding.",
            Scope = "openid mitid transaction_token",

            // The only step that sends one. The broker limits the transaction-text flow to
            // signed requests, which is why this exists and why CAP-022 could not settle the
            // text claims: docs/research/signed-requests.md.
            SignRequest = true,
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["idp_params"] = """
                    {"mitid":{"transaction_text":"U3R1YklEIHRyYW5zYWN0aW9uIHRleHQgb25l","transaction_text_type":"text"}}
                    """,
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
            Step = "Step 12a",
            Title = "Single sign-on, first client",
            Settles = "Establishes the session the next step rides. On its own it is an "
                + "ordinary login; its value is what CAP-029 does afterwards.",
            Operator = "Approve as normal.",
            Client = ClientProfile.SsoA,
        },
        new()
        {
            Id = "CAP-029",
            Step = "Step 12b",
            Title = "Single sign-on, second client",
            Settles = "Whether a second client joined to the same service provider is waved "
                + "through without a prompt, and whether the subject differs per client while "
                + "mitid.uuid stays the same. StubID derives its subject per organisation, and "
                + "that behaviour currently rests on documentation alone.",
            Operator = "Nothing. It should complete without asking. If it asks, single sign-on "
                + "did not apply and that is the finding - say so rather than approving.",
            Client = ClientProfile.SsoB,
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
            Id = "CAP-030",
            Step = "Step 15b",
            Title = "Hybrid response, for c_hash",
            Settles = "c_hash, which ASP.NET Core requires whenever an id_token arrives "
                + "through the front channel. The earlier front-channel recording used a "
                + "response type of id_token alone, which produces neither c_hash nor at_hash, "
                + "so this is the only way to see it.",
            Operator = "Approve as normal.",
            Client = ClientProfile.Hybrid,
            ResponseType = "id_token code",
            ResponseMode = "form_post",
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

    /// <summary>
    /// The steps a run is about, keeping the catalogue's order.
    /// </summary>
    /// <remarks>
    /// A sitting after the first one wants one step, not twelve. The launchpad listing a step
    /// whose fixture is already committed invites a click that stages a second copy of it,
    /// which /finish writes beside the first and the manifest then covers as though both were
    /// meant. Naming the steps is cheaper than remembering not to click.
    /// </remarks>
    public static IReadOnlyList<ManualCase> Selected(IReadOnlyCollection<string>? only) =>
        only is null || only.Count == 0
            ? All
            : [.. All.Where(c => only.Contains(c.Id, StringComparer.OrdinalIgnoreCase))];
}
