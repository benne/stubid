using StubId.Abstractions;
using StubId.Server.Sessions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// Composes the claims the broker sends, in the order it sends them and with the JSON types
/// it uses.
/// </summary>
/// <remarks>
/// <para>
/// Everything here comes from recordings of a real login, not from documentation. That
/// distinction earned its keep: the first version of this class was written from the vendor's
/// claim tables and was wrong in eight ways at once. It omitted <c>nbf</c>, <c>sid</c>,
/// <c>acr</c>, <c>at_hash</c>, <c>idp_transaction_id</c>, <c>idtoken_type</c> and
/// <c>subject_type</c> — four of which appear in no vendor table at all — emitted
/// <c>idp_environment</c>, which the broker does not send, typed <c>session_expiry</c> as a
/// number where the broker sends a string, and put the members in an order resembling no part
/// of the real one.
/// </para>
/// <para>
/// Every one of those tokens validated. A client library would have accepted all of them.
/// </para>
/// </remarks>
public sealed class Tokens(Keys keys, TimeProvider clock)
{
    private static readonly Guid SubjectNamespace = new("6f9b1c34-2d5e-4a71-8c93-1e5b7a2d4f60");

    private readonly JwsWriter _writer = new();

    public const int IdTokenLifetimeSeconds = 300;
    public const int AccessTokenLifetimeSeconds = 10800;
    public const int SessionLifetimeSeconds = 16200;

    /// <summary>
    /// Six years, and the same count on all three recordings. What a signed transaction is for
    /// is being evidence later, so it outlives the id_token beside it by a factor of six
    /// hundred thousand and the access token by seventeen thousand.
    /// </summary>
    /// <remarks>
    /// Taken as a constant rather than as <c>AddYears(6)</c>. The three recordings fall inside
    /// one week, so they span the same leap days and cannot tell the two apart; the seconds are
    /// what was observed.
    /// </remarks>
    public const int TransactionTokenLifetimeSeconds = 189_388_800;

    /// <summary>
    /// The id_token, in the recorded member order.
    /// </summary>
    /// <remarks>
    /// One hash claim at most, in the slot after <c>nonce</c>. The back-channel token carries
    /// <c>at_hash</c> over the access token; the front-channel token of a hybrid response
    /// carries <c>c_hash</c> over the code instead; a front-channel token from a response type
    /// of <c>id_token</c> alone carries neither, because there is neither to cover.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-024/token/id_token.payload.json")]
    public string IdToken(
        string issuer,
        IssuedCode code,
        string? accessToken,
        string organisation,
        string? authorizationCode = null)
    {
        var now = clock.GetUtcNow();
        var citizen = code.Citizen;
        var subject = Subject(organisation, citizen);
        var level = Nsis(citizen.Loa);

        List<JsonClaim> claims =
        [
            JsonClaim.String("iss", issuer),
            JsonClaim.Number("nbf", now.ToUnixTimeSeconds()),
            JsonClaim.Number("iat", now.ToUnixTimeSeconds()),
            JsonClaim.Number("exp", now.AddSeconds(IdTokenLifetimeSeconds).ToUnixTimeSeconds()),
            JsonClaim.String("aud", code.Request.ClientId),
            JsonClaim.Strings("amr", citizen.Amr),
        ];

        if (code.Request.Nonce is not null)
        {
            claims.Add(JsonClaim.String("nonce", code.Request.Nonce));
        }

        if (accessToken is not null)
        {
            claims.Add(JsonClaim.String("at_hash", HashClaims.Compute(accessToken)));
        }
        else if (authorizationCode is not null)
        {
            claims.Add(JsonClaim.String("c_hash", HashClaims.Compute(authorizationCode)));
        }

        claims.AddRange(
        [
            // The same value under two names. Both are sent; neither is redundant to a client
            // that looks for only one of them.
            JsonClaim.String("sid", code.SessionId),
            JsonClaim.String("sub", subject),
            JsonClaim.Number("auth_time", code.AuthenticatedAt.ToUnixTimeSeconds()),
            JsonClaim.String("idp", "mitid"),
            JsonClaim.String("acr", level),
            JsonClaim.String("neb_sid", code.SessionId),
            JsonClaim.String("loa", level),
            JsonClaim.String("aal", level),
            JsonClaim.String("ial", level),
            JsonClaim.String("identity_type", "private"),
            JsonClaim.String("transaction_id", code.TransactionId),
            JsonClaim.String("idp_transaction_id", code.IdpTransactionId),

            // A unix timestamp, sent as a string. The id_token's other timestamps are numbers.
            JsonClaim.String("session_expiry",
                now.AddSeconds(SessionLifetimeSeconds).ToUnixTimeSeconds()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("idtoken_type", "strict"),
            JsonClaim.String("subject_type", "org_mapped"),
        ]);

        return _writer.Sign(claims, keys.TokenSigning);
    }

    /// <summary>
    /// The userinfo response, in the recorded member order. Every value is a JSON string,
    /// including the age and the two booleans.
    /// </summary>
    /// <remarks>
    /// The documented <c>session_status</c> and <c>session_identifier</c> do not appear at
    /// all: the wire carries <c>session_is_active</c> and <c>session_expiry</c>. The subject
    /// comes last rather than first, and two claims nobody documented — <c>mitid.psd2</c> and
    /// <c>mitid.geo_ip_distance_km</c> — are always present.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-021/userinfo/response.raw")]
    public IReadOnlyList<JsonClaim> UserInfo(string organisation, IssuedAccessToken token)
    {
        var now = clock.GetUtcNow();
        var citizen = token.Citizen;
        var scopes = token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var level = Nsis(citizen.Loa);

        List<JsonClaim> claims =
        [
            JsonClaim.String("session_is_active", "true"),
            JsonClaim.String("session_expiry",
                now.AddSeconds(SessionLifetimeSeconds).ToUnixTimeSeconds()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("idp", "mitid"),
            JsonClaim.String("subject_type", "org_mapped"),
            JsonClaim.String("idp_identity_id", citizen.Uuid),
            JsonClaim.String("loa", level),
            JsonClaim.String("aal", level),
            JsonClaim.String("ial", level),
            JsonClaim.String("mitid.transaction_id", token.IdpTransactionId),
            JsonClaim.String("mitid.uuid", citizen.Uuid),
            JsonClaim.String("mitid.age",
                citizen.Age(now).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("mitid.date_of_birth", citizen.DateOfBirth),
            JsonClaim.String("mitid.has_cpr", "true"),
            JsonClaim.String("mitid.identity_name", citizen.Name),
        ];

        if (scopes.Contains("ssn"))
        {
            claims.Add(JsonClaim.String("dk.cpr", citizen.Cpr));
        }

        if (scopes.Contains("nemid.pid"))
        {
            claims.AddRange(
            [
                JsonClaim.String("nemid.pid", citizen.Pid),
                JsonClaim.String("nemid.pid_status", "success"),
            ]);
        }

        if (scopes.Any(s => s.StartsWith("ssn.details", StringComparison.Ordinal)))
        {
            // The recorded identity had no register entry behind it, so the broker answered
            // with a status and nothing else. Which of the address members appear for an
            // identity that does is unobserved.
            claims.Add(JsonClaim.String("ssn.details.status", "unable_to_lookup"));
        }

        claims.AddRange(
        [
            JsonClaim.String("mitid.psd2", "false"),
            JsonClaim.String("mitid.geo_ip_distance_km", "8396"),
        ]);

        if (scopes.Contains("ssn"))
        {
            // Base64, and shown to the user when the broker asks for their CPR.
            claims.AddRange(
            [
                JsonClaim.String("mitid.cpr_consent_text", CprConsentText),
                JsonClaim.String("mitid.cpr_consent_header", CprConsentHeader),
            ]);
        }

        claims.Add(JsonClaim.String("sub", Subject(organisation, citizen)));

        return claims;
    }

    /// <summary>
    /// The userinfo token: the same identity as the userinfo endpoint, signed, returned in
    /// the token response.
    /// </summary>
    /// <remarks>
    /// It arrives whenever the client has the setting switched on, not because a scope asked
    /// for it - the recording carries one from a plain <c>openid mitid</c> login. Its header
    /// says <c>at+jwt</c> rather than <c>JWT</c>, its assurance claims come in a different
    /// order from the id_token's, and its <c>auth_time</c> is a string where the id_token
    /// sends a number. Same broker, same response, two answers.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-024/token/userinfo_token.payload.json")]
    public string UserInfoToken(string issuer, IssuedCode code, string organisation)
    {
        var now = clock.GetUtcNow();
        var citizen = code.Citizen;
        var level = Nsis(citizen.Loa);

        return _writer.Sign(
        [
            JsonClaim.String("iss", issuer),
            JsonClaim.Number("nbf", now.ToUnixTimeSeconds()),
            JsonClaim.Number("iat", now.ToUnixTimeSeconds()),
            JsonClaim.Number("exp", now.AddSeconds(IdTokenLifetimeSeconds).ToUnixTimeSeconds()),
            JsonClaim.Strings("amr", citizen.Amr),
            JsonClaim.String("mitid.transaction_id", code.IdpTransactionId),
            JsonClaim.String("mitid.uuid", citizen.Uuid),
            JsonClaim.String("mitid.age",
                citizen.Age(now).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("mitid.date_of_birth", citizen.DateOfBirth),
            JsonClaim.String("mitid.has_cpr", "true"),
            JsonClaim.String("mitid.identity_name", citizen.Name),
            JsonClaim.String("loa", level),
            JsonClaim.String("ial", level),
            JsonClaim.String("aal", level),
            JsonClaim.String("identity_type", "private"),
            JsonClaim.String("idp_identity_id", citizen.Uuid),
            JsonClaim.String("idp", "mitid"),
            JsonClaim.String("acr", level),
            JsonClaim.String("auth_time", code.AuthenticatedAt.ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("sub", Subject(organisation, citizen)),
            JsonClaim.String("transaction_id", code.TransactionId),
            JsonClaim.String("aud", code.Request.ClientId),
        ], keys.TokenSigning, type: "at+jwt");
    }

    /// <summary>
    /// The transaction token, in the recorded member order. Returned when the request asked
    /// for the <c>transaction_token</c> scope, and signed with a different key from the other
    /// three tokens of the same response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim set is close to the userinfo token's and the types are not. <c>amr</c> is a
    /// bare string here and an array in the other three tokens of the same response;
    /// <c>auth_time</c> is a string beside numeric <c>nbf</c>, <c>iat</c> and <c>exp</c>;
    /// <c>loa</c>, <c>ial</c>, <c>aal</c> come in that order rather than the id_token's
    /// <c>loa</c>, <c>aal</c>, <c>ial</c>; and the four lifetime and audience claims move from
    /// the front of the token to the end.
    /// </para>
    /// <para>
    /// <c>auth_time</c> is the authentication, not the issue: CAP-021 carries
    /// <c>"1788129601"</c> beside an <c>iat</c> of <c>1788129602</c>. The other two recordings
    /// were issued in the same second as the login and hide it.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-021/token/transaction_token.payload.json, "
                   + "fixtures/neb/pp-session/CAP-022/token/transaction_token.payload.json")]
    public string TransactionToken(string issuer, IssuedCode code, string organisation)
    {
        var now = clock.GetUtcNow();
        var citizen = code.Citizen;
        var scopes = code.Request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var level = Nsis(citizen.Loa);

        List<JsonClaim> claims =
        [
            JsonClaim.String("mitid.transaction_id", code.IdpTransactionId),
            JsonClaim.String("mitid.uuid", citizen.Uuid),
            JsonClaim.String("mitid.age",
                citizen.Age(now).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("mitid.date_of_birth", citizen.DateOfBirth),
            JsonClaim.String("mitid.has_cpr", "true"),
            JsonClaim.String("mitid.identity_name", citizen.Name),

            // A bare string. Every other token in the same response sends an array.
            JsonClaim.String("amr", citizen.Amr),
            JsonClaim.String("loa", level),
            JsonClaim.String("ial", level),
            JsonClaim.String("aal", level),
            JsonClaim.String("identity_type", "private"),
            JsonClaim.String("idp_identity_id", citizen.Uuid),
            JsonClaim.String("idp", "mitid"),
        ];

        if (scopes.Contains("ssn"))
        {
            claims.Add(JsonClaim.String("dk.cpr", citizen.Cpr));
        }

        if (scopes.Contains("nemid.pid"))
        {
            claims.AddRange(
            [
                JsonClaim.String("nemid.pid", citizen.Pid),
                JsonClaim.String("nemid.pid_status", "success"),
            ]);
        }

        claims.Add(JsonClaim.String("acr", level));

        if (scopes.Any(s => s.StartsWith("ssn.details", StringComparison.Ordinal)))
        {
            claims.Add(JsonClaim.String("ssn.details.status", "unable_to_lookup"));
        }

        claims.AddRange(
        [
            JsonClaim.String("auth_time", code.AuthenticatedAt.ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonClaim.String("sub", Subject(organisation, citizen)),
            JsonClaim.String("transaction_id", code.TransactionId),

            // Where the vendor documents recipient_info, which is not sent.
            JsonClaim.String("redirect_uri", code.Request.RedirectUri),
        ]);

        // Every recording carried one. A request without a nonce is unobserved here, and
        // omitting the claim is the same choice the id_token makes.
        if (code.Request.Nonce is not null)
        {
            claims.Add(JsonClaim.String("nonce", code.Request.Nonce));
        }

        claims.AddRange(
        [
            JsonClaim.String("requested_scope", code.Request.Scope),
            JsonClaim.String("mitid.psd2", "false"),
            JsonClaim.String("mitid.geo_ip_distance_km", "8396"),
        ]);

        if (scopes.Contains("ssn"))
        {
            claims.AddRange(
            [
                JsonClaim.String("mitid.cpr_consent_text", CprConsentText),
                JsonClaim.String("mitid.cpr_consent_header", CprConsentHeader),
            ]);
        }

        claims.AddRange(
        [
            TransactionActions(scopes),
            JsonClaim.String("transaction_client_ip", code.ClientIp),
            JsonClaim.Number("nbf", now.ToUnixTimeSeconds()),
            JsonClaim.Number("exp", now.AddSeconds(TransactionTokenLifetimeSeconds).ToUnixTimeSeconds()),
            JsonClaim.Number("iat", now.ToUnixTimeSeconds()),
            JsonClaim.String("iss", issuer),
            JsonClaim.String("aud", code.Request.ClientId),
        ]);

        return _writer.Sign(claims, keys.TransactionSigning);
    }

    /// <summary>
    /// The OCSP response that travels beside the transaction token, saying <c>good</c> about
    /// the certificate that signed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard base64, padded, where every other encoded value in the same response is
    /// base64url — and signed with ECDSA on P-256 where the token beside it is RS256.
    /// </para>
    /// <para>
    /// The pair is never split. All nine recorded token bodies carry both members or neither,
    /// so a response with a transaction token and nothing beside it is a shape the broker has
    /// never sent.
    /// </para>
    /// <para>
    /// One difference from the recordings, and it is deliberate: the broker serves an answer it
    /// already had — CAP-031's <c>producedAt</c> is three and a half minutes before the
    /// recording that carries it — where this mints one per response. Caching an answer to
    /// reproduce the staleness would make a stub's output depend on how long it had been
    /// running, which is the opposite of what a test wants.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Shape, FidelityProvenance.Divergent,
        Evidence = "fixtures/neb/pp-session/CAP-021/token/response.raw, "
                   + "fixtures/neb/pp-session/CAP-022/token/response.raw, "
                   + "fixtures/neb/pp-session/CAP-031/token/response.raw",
        Reason = "docs/brokers/neb/divergences.md#the-oces3-certificate-chain")]
    public string TransactionTokenOcspResponse() =>
        Convert.ToBase64String(OcspWriter.Good(
            keys.TransactionSigning.Certificate, keys.OcspResponder, clock.GetUtcNow()));

    /// <summary>
    /// What the login did, besides authenticate. A bare string when there is one action and an
    /// array when there is more than one, which is the broker's own inconsistency rather than
    /// a choice made here.
    /// </summary>
    /// <remarks>
    /// <c>mitid.login</c> is recorded on all three. The second entry is inferred: CAP-021 asked
    /// for <c>ssn</c> and got <c>mitid.cpr_match</c> beside it, and scope is the only thing the
    /// token endpoint knows — StubID's CPR match happens at its own endpoint, after this token
    /// would have been issued. So the rule is the one already applied to <c>dk.cpr</c> and the
    /// consent pair, and it is a reading of one recording rather than a recorded rule.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.Assumed,
        Evidence = "fixtures/neb/pp-session/CAP-021/token/transaction_token.payload.json, "
                   + "fixtures/neb/pp-session/CAP-022/token/transaction_token.payload.json",
        AwaitingCapture = "The string-or-array form is recorded; deriving mitid.cpr_match from "
                          + "the ssn scope is not. A login asking for ssn that does not match a "
                          + "CPR, or a CPR match without the scope, would settle it. CAP-021 did "
                          + "both at once, so it cannot say which of the two put the action in "
                          + "the token.")]
    private static JsonClaim TransactionActions(string[] scopes)
    {
        List<string> actions = ["mitid.login"];

        if (scopes.Contains("ssn"))
        {
            actions.Add("mitid.cpr_match");
        }

        return actions.Count == 1
            ? JsonClaim.String("transaction_actions", actions[0])
            : JsonClaim.Strings("transaction_actions", [.. actions]);
    }

    /// <summary>
    /// The subject differs per receiving organisation while the MitID identifier stays the
    /// same, and it is derived rather than stored so it survives a restart.
    /// </summary>
    /// <remarks>
    /// Scoped to the organisation, not the client. Two clients joined to one service provider
    /// were recorded receiving the same subject for the same person, which is what the
    /// id_token means by <c>subject_type: org_mapped</c>. Deriving it per client — which this
    /// did until the recording showed otherwise — hands an application that signs users in
    /// through two of its own clients two different people.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-029/token/id_token.payload.json")]
    public static string Subject(string organisation, Citizen citizen) =>
        Uuid5.Create(SubjectNamespace, $"{organisation}|{citizen.Uuid}").ToString();

    private static string Nsis(string level) => $"https://data.gov.dk/concept/core/nsis/{level}";

    /// <summary>The prompt shown when the broker asks a user for their personal number.</summary>
    /// <remarks>Base64, and the broker's own wording rather than a property of any person.</remarks>
    private const string CprConsentHeader = "SW5kdGFzdCBkaXQgQ1BSLW51bW1lcg==";

    private const string CprConsentText = "U3R1YklEIGVtdWxlcmVyIE1pdElEIGkgdGVzdA==";
}
