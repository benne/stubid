using System.Collections.Concurrent;
using System.Security.Cryptography;
using StubId.Abstractions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>A parked authorization request, whether it arrived by PAR or by redirect.</summary>
public sealed record AuthorizationRequest(
    string ClientId,
    string RedirectUri,
    string ResponseType,
    string ResponseMode,
    string Scope,
    string? State,
    string? Nonce,
    string? CodeChallenge,
    string? CodeChallengeMethod);

/// <summary>An issued authorization code and everything the token endpoint needs to redeem it.</summary>
public sealed record IssuedCode(
    AuthorizationRequest Request,
    Citizen Citizen,
    DateTimeOffset AuthenticatedAt,
    string SessionId);

/// <summary>An access token and the identity behind it.</summary>
public sealed record IssuedAccessToken(Citizen Citizen, string Scope, string SessionId, DateTimeOffset AuthenticatedAt);

/// <summary>
/// Everything the slice remembers. In memory, single tenant, deliberately small.
/// </summary>
public sealed class BrokerState
{
    private readonly ConcurrentDictionary<string, AuthorizationRequest> _pushed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedAccessToken> _accessTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// The clients the broker publishes for anyone to use against pre-production, so an
    /// existing configuration reaches StubID by changing the authority alone.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Clients { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["0a775a87-878c-4b83-abe3-ee29c720c3e7"] = ["code"],
        ["c0beb4dc-69d1-4316-8167-2d0a62816103"] = ["id_token code"],
        ["93ed8e0d-93ad-405c-b1ac-8bf13d484941"] = ["id_token"],
    };

    /// <summary>
    /// The person every login authenticates as, until citizens can be created. The personal
    /// number is a replacement number, so it cannot collide with a real one.
    /// </summary>
    public Citizen DefaultCitizen { get; } = new(
        Uuid: "1a5f8c2e-0b47-4d9a-9f31-6c2e8b7a4d15",
        Name: "Anders Berg Christiansen",
        DateOfBirth: "1985-03-29",
        Cpr: "8903851234");

    /// <summary>
    /// Any non-empty secret is accepted. A stub cannot know the secret an existing
    /// configuration already carries, and demanding a particular one would defeat the point
    /// of changing only the authority. A missing secret is still refused, because telling
    /// "authenticated badly" from "did not authenticate" is behaviour worth keeping.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.Divergent,
        Reason = "docs/brokers/neb/divergences.md#client-secrets",
        Evidence = "fixtures/neb/pp/CAP-014")]
    public bool IsKnownClient(string? clientId) =>
        clientId is not null && Clients.ContainsKey(clientId);

    public string PushRequest(AuthorizationRequest request)
    {
        var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pushed[reference] = request;
        return $"urn:ietf:params:oauth:request_uri:{reference}";
    }

    public AuthorizationRequest? RedeemPushedRequest(string requestUri)
    {
        var reference = requestUri.Split(':').LastOrDefault() ?? "";
        return _pushed.TryRemove(reference, out var request) ? request : null;
    }

    public string IssueCode(AuthorizationRequest request, Citizen citizen, DateTimeOffset now)
    {
        var code = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new IssuedCode(request, citizen, now, Guid.NewGuid().ToString());
        return code;
    }

    /// <summary>Codes are single use: redeeming one removes it.</summary>
    public IssuedCode? RedeemCode(string code) =>
        _codes.TryRemove(code, out var issued) ? issued : null;

    public string IssueAccessToken(IssuedCode code)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _accessTokens[token] = new IssuedAccessToken(
            code.Citizen, code.Request.Scope, code.SessionId, code.AuthenticatedAt);
        return token;
    }

    public IssuedAccessToken? ReadAccessToken(string token) =>
        _accessTokens.TryGetValue(token, out var issued) ? issued : null;
}
