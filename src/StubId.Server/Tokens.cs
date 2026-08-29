using StubId.Abstractions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// Composes the claims the broker sends, in the order it sends them and with the JSON types
/// it uses.
/// </summary>
/// <remarks>
/// Claim names, order and types come from the recorded documentation rather than from what
/// looks reasonable. Where the broker's own sources disagree, the choice is marked and the
/// recording that would settle it is named.
/// </remarks>
public sealed class Tokens(Keys keys, TimeProvider clock)
{
    private static readonly Guid SubjectNamespace = new("6f9b1c34-2d5e-4a71-8c93-1e5b7a2d4f60");

    private readonly JwsWriter _writer = new();

    public const int IdTokenLifetimeSeconds = 300;
    public const int AccessTokenLifetimeSeconds = 10800;

    /// <summary>
    /// The claim set is the broker's documented one. A real token also carries nbf and sid,
    /// which its claim tables omit entirely; both are held back until a recorded login says
    /// where they sit, since adding a claim a client ignores is harmless and guessing at
    /// member order is not.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.DocsConflict, AwaitingCapture = "CAP-020")]
    public string IdToken(string issuer, IssuedCode code)
    {
        var now = clock.GetUtcNow();
        var citizen = code.Citizen;

        return _writer.Sign(
        [
            JsonClaim.String("iss", issuer),
            JsonClaim.String("neb_sid", code.SessionId),
            JsonClaim.String("sub", Subject(code.Request.ClientId, citizen)),
            JsonClaim.String("aud", code.Request.ClientId),
            JsonClaim.Number("exp", now.AddSeconds(IdTokenLifetimeSeconds).ToUnixTimeSeconds()),
            JsonClaim.Number("iat", now.ToUnixTimeSeconds()),
            JsonClaim.Number("auth_time", code.AuthenticatedAt.ToUnixTimeSeconds()),
            .. code.Request.Nonce is null ? Array.Empty<JsonClaim>() : [JsonClaim.String("nonce", code.Request.Nonce)],
            JsonClaim.String("idp", "mitid"),
            JsonClaim.String("idp_environment", "test"),
            JsonClaim.String("identity_type", "private"),
            JsonClaim.String("transaction_id", Guid.NewGuid().ToString()),
            JsonClaim.Number("session_expiry", now.AddSeconds(AccessTokenLifetimeSeconds).ToUnixTimeSeconds()),
            JsonClaim.String("loa", Nsis(citizen.Loa)),
            JsonClaim.String("ial", Nsis(citizen.Loa)),
            JsonClaim.String("aal", Nsis(citizen.Loa)),
            JsonClaim.Strings("amr", citizen.Amr),
        ], keys.TokenSigning);
    }

    /// <summary>
    /// Every value is a JSON string, including the age and the has_cpr flag. A client that
    /// parses either as a number or a boolean fails against the real broker.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.DocsConfirmed, AwaitingCapture = "CAP-021")]
    public IReadOnlyList<JsonClaim> UserInfo(string clientId, IssuedAccessToken token)
    {
        var citizen = token.Citizen;
        var scopes = token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var claims = new List<JsonClaim>
        {
            JsonClaim.String("sub", Subject(clientId, citizen)),
        };

        if (scopes.Contains("mitid"))
        {
            claims.AddRange(
            [
                JsonClaim.String("mitid.uuid", citizen.Uuid),
                JsonClaim.String("mitid.date_of_birth", citizen.DateOfBirth),
                JsonClaim.String("mitid.age", citizen.Age(clock.GetUtcNow()).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                JsonClaim.String("mitid.has_cpr", "true"),
                JsonClaim.String("mitid.identity_name", citizen.Name),
                JsonClaim.String("mitid.transaction_id", Guid.NewGuid().ToString()),
            ]);
        }

        if (scopes.Contains("ssn"))
        {
            claims.Add(JsonClaim.String("dk.cpr", citizen.Cpr));
        }

        claims.AddRange(
        [
            JsonClaim.String("session_status", "active"),
            JsonClaim.String("session_identifier", token.SessionId.ToUpperInvariant()),
            JsonClaim.String("idp_identity_id", citizen.Uuid),
        ]);

        return claims;
    }

    /// <summary>
    /// The subject differs per receiving organisation while the MitID identifier stays the
    /// same, and it is derived rather than stored so it survives a restart.
    /// </summary>
    public static string Subject(string clientId, Citizen citizen) =>
        Uuid5.Create(SubjectNamespace, $"{clientId}|{citizen.Uuid}").ToString();

    private static string Nsis(string level) => $"https://data.gov.dk/concept/core/nsis/{level}";
}
