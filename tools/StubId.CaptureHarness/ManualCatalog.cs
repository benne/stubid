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
public static class ManualCatalog
{
    /// <summary>Every step of a broker's sitting, in the order it has to happen.</summary>
    public static IReadOnlyList<ManualCase> For(Broker broker) => broker switch
    {
        Broker.NetsEidBroker => NetsEidBroker,
        Broker.Signicat => Signicat,
        _ => [],
    };

    /// <summary>"StubID reference text", as the broker wants it: Base64.</summary>
    private const string ReferenceText = "U3R1YklEIHJlZmVyZW5jZSB0ZXh0";

    /// <summary>
    /// The second broker's sitting, in step order, which is also case order here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every step on the primary, claims and hybrid clients signs its request, because those three
    /// refuse a plain query. The partner client takes the aborts and the timeout, which have nothing
    /// to sign for and cost least to lose, and single sign-on, which only it can take: it is the one
    /// client with single sign-on left on.
    /// </para>
    /// <para>
    /// Some of the first broker's sitting has no counterpart. This broker has no response type of
    /// id_token alone and no CPR-match endpoint. End session without an id_token_hint is already in
    /// the unattended pack, and a hint would put a token in a URL that nothing yet strips. An
    /// unregistered redirect URI is refused before any login, so it belongs in that pack too. A
    /// transaction text is signing, which is out of scope for this broker. Assurance level is asked
    /// for once, at High.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ManualCase> Signicat =>
    [
        new()
        {
            Id = "CAP-020",
            Step = "Step S1",
            Title = "Abort inside the MitID widget",
            Settles = "Which OAuth error accompanies a user abort on this broker, and whether it "
                + "redirects back to the client or shows its own page. No code for it can be "
                + "quoted from the published error catalog.",
            Operator = "Start the login, then cancel inside the MitID widget.",
            Client = BrokerClient.Signicat.Partner,
            Scope = "openid profile",
            ExpectCode = false,
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-021",
            Step = "Step S2",
            Title = "Abort at the CPR number prompt",
            Settles = "Whether canceling after MitID has authenticated the person, at the prompt "
                + "for the CPR number, returns the same error as CAP-020, and whether it comes "
                + "back to the client at all.",
            Operator = "Approve in MitID. When the page asking for the CPR number appears, cancel "
                + "there instead of typing it, and note whether that page is MitID's or Signicat's.",
            Client = BrokerClient.Signicat.Partner,
            Scope = "openid profile nin",
            ExpectCode = false,
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-022",
            Step = "Step S3",
            Title = "A login left to time out",
            Settles = "What a login that times out returns, where it lands, and roughly when. The "
                + "research ranked it with the abort as the first thing a sitting has to answer, "
                + "and no error for it can be quoted today.",
            Operator = "In the second browser: start it, type nothing, note the time, and leave it on "
                + "screen while the other steps go ahead. Force it at forty-five minutes if nothing "
                + "has happened.",
            Client = BrokerClient.Signicat.Partner,
            Scope = "openid profile",
            ExpectCode = false,
            FollowUps = [],
        },
        new()
        {
            Id = "CAP-023",
            Step = "Step S4",
            Title = "The baseline login",
            Settles = "The token response and the id_token's member set and order on a client "
                + "with request objects on and ID token user data at StandardScopes; the values "
                + "behind amr, idp and acr, which have only been seen as lengths; and what "
                + "presenting the same code twice does.",
            Operator = "Approve with the app simulator.",
            Client = BrokerClient.Signicat.Primary,
            Scope = "openid profile",
            SignRequest = true,
            FollowUps = [FollowUp.UserInfo, FollowUp.ReplayCode],
        },
        new()
        {
            Id = "CAP-024",
            Step = "Step S5",
            Title = "The login with the CPR number",
            Settles = "nin and its two companion claims at userinfo, with their values and JSON "
                + "types; idp_id; and whether asking for them changes the id_token.",
            Operator = "Approve, then type the identity's CPR number when asked.",
            Client = BrokerClient.Signicat.Primary,
            Scope = "openid profile nin idp-id",
            SignRequest = true,
        },
        new()
        {
            Id = "CAP-025",
            Step = "Step S6",
            Title = "Every claim in the identity token",
            Settles = "Where MitID's own claims land on a client with ID token user data set to "
                + "All and the mitid-extra scope: whether nin moves into the id_token, and which "
                + "mitid_* claims exist at all. None has been seen from this broker.",
            Operator = "Approve, then type the identity's CPR number when asked.",
            Client = BrokerClient.Signicat.Claims,
            Scope = "openid profile nin idp-id mitid-extra",
            SignRequest = true,
        },
        new()
        {
            Id = "CAP-026",
            Step = "Step S7",
            Title = "Transaction consent with a reference text",
            Settles = "Whether a reference text comes back to the client anywhere - in a claim, "
                + "at userinfo, or not at all - and in what form. The day-zero probe proved it "
                + "reaches the app; what the relying party receives has not been seen.",
            Operator = "Before approving, expand the simulator's Flow Value Texts and check that "
                + "Reference Text reads StubID reference text, exactly.",
            Client = BrokerClient.Signicat.Primary,
            Scope = "openid profile",

            // Replaces the target's own acr_values rather than adding to it, so idp:mitid is
            // written again here.
            SignRequest = true,
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acr_values"] = $"idp:mitid mitid_reference_text:{ReferenceText}",
            },
        },
        new()
        {
            Id = "CAP-027",
            Step = "Step S8",
            Title = "Single sign-on, on a second client",
            Settles = "Whether a second client on the same account is waved through without a "
                + "prompt, riding the session CAP-026 left on a client that forces login, and "
                + "whether sub and sid come back the same as CAP-026's. Discovery advertises "
                + "public subjects, where the first broker scopes a subject to the organization.",
            Operator = "Start it straight after S7, in the same browser. It should complete without "
                + "asking. If it asks, single sign-on did not apply and that is the finding - say "
                + "so rather than approving.",
            Client = BrokerClient.Signicat.Partner,
            Scope = "openid profile",
            ForcesLogin = false,
        },
        new()
        {
            Id = "CAP-028",
            Step = "Step S9",
            Title = "Hybrid response, for c_hash",
            Settles = "c_hash in the id_token that arrives through the front channel, at_hash in the "
                + "one the token endpoint returns - where the first broker's hybrid recording has "
                + "it - and the form_post envelope. This broker advertises no response type of "
                + "id_token alone, so this is the only front-channel id_token it can show.",
            Operator = "Approve as normal.",
            Client = BrokerClient.Signicat.Hybrid,
            Scope = "openid profile",
            ResponseType = "code id_token",
            ResponseMode = "form_post",
            SignRequest = true,
        },
        new()
        {
            Id = "CAP-029",
            Step = "Step S10",
            Title = "Assurance level High",
            Settles = "Whether loa:high changes acr, amr, mitid_loa and mitid_aal and the approval "
                + "MitID asks for. On the claims client, so it compares with CAP-025. Signicat "
                + "documents the key for every eID on its platform, lists MitID as supporting all "
                + "three levels, and says a login below the requested level fails; no request has "
                + "used it yet, and unknown keys are ignored here, so unchanged levels would mean "
                + "it was not read.",
            Operator = "Approve at the highest level the app simulator offers. If it offers "
                + "nothing above the ordinary approval, cancel and say what it did offer.",
            Client = BrokerClient.Signicat.Claims,
            Scope = "openid profile mitid-extra",
            SignRequest = true,
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acr_values"] = "idp:mitid loa:high",
            },
        },
    ];

    /// <summary>Everything the private client is entitled to ask for.</summary>
    private const string FullScope =
        "openid mitid ssn nemid.pid ssn.details_name ssn.details_address userinfo_token transaction_token";

    /// <summary>
    /// In the order the sitting has to run, which is the step order rather than the case
    /// number. A login establishes a broker session, so the steps that record a refusal or an
    /// abort have to happen before the first successful one or they record something else.
    /// </summary>
    private static IReadOnlyList<ManualCase> NetsEidBroker =>
    [
        new()
        {
            Id = "CAP-023",
            Step = "Step 2",
            Title = "Abort inside the MitID widget",
            Settles = "Which OAuth error accompanies a user abort, and whether the broker "
                + "redirects it back to the client or shows its own page.",
            Operator = "Start the login, then cancel inside the MitID widget.",
            Client = BrokerClient.NetsEidBroker.Private,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.Restricted,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.Private,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.Private,
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
            Client = BrokerClient.NetsEidBroker.Private,
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
            Operator = "Read the transaction text on the broker's page, in the panel beside "
                + "the MitID widget, and check it word for word before approving. Then note "
                + "what the app simulator's flow values say: on CAP-031 the text was not "
                + "among them.",
            Client = BrokerClient.NetsEidBroker.Private,
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
            Client = BrokerClient.NetsEidBroker.Private,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.SsoA,
            Scope = "openid mitid",
        },
        new()
        {
            Id = "CAP-029",
            Step = "Step 12b",
            Title = "Single sign-on, second client",
            Settles = "Whether a second client joined to the same service provider is waved "
                + "through without a prompt, and whether the subject differs per client while "
                + "mitid.uuid stays the same. StubID derives its subject per organization, and "
                + "that behavior currently rests on documentation alone.",
            Operator = "Nothing. It should complete without asking. If it asks, single sign-on "
                + "did not apply and that is the finding - say so rather than approving.",
            Client = BrokerClient.NetsEidBroker.SsoB,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.OpenImplicit,
            Scope = "openid mitid",
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
            Client = BrokerClient.NetsEidBroker.Hybrid,
            Scope = "openid mitid",
            ResponseType = "id_token code",
            ResponseMode = "form_post",
        },
        new()
        {
            Id = "CAP-027",
            Step = "Step 16",
            Title = "End session",
            Settles = "What end session does with and without an id_token_hint, and whether "
                + "post_logout_redirect_uri is honored either way.",
            Operator = "Follow the logout through to wherever it lands.",
            Client = BrokerClient.NetsEidBroker.Private,
            Scope = "openid mitid",
            FollowUps = [FollowUp.EndSession],
        },
    ];

    /// <summary>
    /// The steps that cannot be recorded while a setting is missing.
    /// </summary>
    /// <remarks>
    /// Read from the roster rather than from the setting's spelling: which registrations name it,
    /// and which steps use those registrations. What this replaced asked whether the name
    /// contained "SSO_A", which was the roster written a second time in string matching and could
    /// only ever describe one broker's naming.
    /// </remarks>
    public static IReadOnlyList<string> StepsNeeding(BrokerTarget target, string setting)
    {
        var clients = target.Clients
            .Where(client => client.IdSetting == setting || client.SecretSetting == setting)
            .ToList();

        // The key signs every signed step on a broker that signs with one, whichever client the
        // step records with. Not its identifier: without one the object is still signed, and a
        // client holding a single key may still resolve it. The first broker names no key setting.
        var signs = setting == target.PrivateKeySetting;

        return [.. For(target.Broker)
            .Where(c => clients.Contains(c.Client) || (signs && c.SignRequest))
            .Select(c => c.Id)];
    }

    /// <summary>
    /// The steps a run is about, keeping the catalog's order.
    /// </summary>
    /// <remarks>
    /// A sitting after the first one wants one step, not twelve. The launchpad listing a step
    /// whose fixture is already committed invites a click that stages a second copy of it,
    /// which /finish writes beside the first and the manifest then covers as though both were
    /// meant. Naming the steps is cheaper than remembering not to click.
    /// </remarks>
    public static IReadOnlyList<ManualCase> Selected(Broker broker, IReadOnlyCollection<string>? only) =>
        only is null || only.Count == 0
            ? For(broker)
            : [.. For(broker).Where(c => only.Contains(c.Id, StringComparer.OrdinalIgnoreCase))];
}
