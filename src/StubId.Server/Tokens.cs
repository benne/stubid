using StubId.Abstractions;
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
    /// The id_token, in the recorded member order.
    /// </summary>
    /// <remarks>
    /// <c>at_hash</c> appears only when an access token is issued alongside: the recorded
    /// front-channel id_token, from a response type of <c>id_token</c> on its own, carries
    /// neither it nor <c>c_hash</c>.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-024/token/id_token.payload.json")]
    public string IdToken(string issuer, IssuedCode code, string? accessToken)
    {
        var now = clock.GetUtcNow();
        var citizen = code.Citizen;
        var subject = Subject(code.Request.ClientId, citizen);
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
    public IReadOnlyList<JsonClaim> UserInfo(string clientId, IssuedAccessToken token)
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
                JsonClaim.String("mitid.cpr_consent_text", citizen.CprConsentText),
                JsonClaim.String("mitid.cpr_consent_header", citizen.CprConsentHeader),
            ]);
        }

        claims.Add(JsonClaim.String("sub", Subject(clientId, citizen)));

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
    public string UserInfoToken(string issuer, IssuedCode code)
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
            JsonClaim.String("sub", Subject(code.Request.ClientId, citizen)),
            JsonClaim.String("transaction_id", code.TransactionId),
            JsonClaim.String("aud", code.Request.ClientId),
        ], keys.TokenSigning, type: "at+jwt");
    }

    /// <summary>
    /// The subject differs per receiving organisation while the MitID identifier stays the
    /// same, and it is derived rather than stored so it survives a restart. The broker names
    /// this arrangement itself, in the id_token's <c>subject_type</c> claim: org_mapped.
    /// </summary>
    public static string Subject(string clientId, Citizen citizen) =>
        Uuid5.Create(SubjectNamespace, $"{clientId}|{citizen.Uuid}").ToString();

    private static string Nsis(string level) => $"https://data.gov.dk/concept/core/nsis/{level}";
}
