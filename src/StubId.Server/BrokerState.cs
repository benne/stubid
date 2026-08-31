using System.Collections.Concurrent;
using System.Security.Cryptography;
using StubId.Abstractions;
using StubId.Server.Sessions;
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
/// <param name="TransactionId">The broker's own identifier for the exchange.</param>
/// <param name="IdpTransactionId">
/// The identity provider's, which differs from it. Both are sent, and the userinfo response
/// reports the second under mitid.transaction_id.
/// </param>
public sealed record IssuedCode(
    AuthorizationRequest Request,
    Citizen Citizen,
    DateTimeOffset AuthenticatedAt,
    string SessionId,
    string TransactionId,
    string IdpTransactionId);

/// <summary>An access token, the identity behind it, and the client that obtained it.</summary>
/// <remarks>
/// The client matters: the subject is scoped to the receiving organisation, so userinfo has
/// to answer with the same subject the id_token carried, and that depends on who is asking.
/// </remarks>
public sealed record IssuedAccessToken(
    string ClientId,
    Citizen Citizen,
    string Scope,
    string SessionId,
    string IdpTransactionId,
    DateTimeOffset AuthenticatedAt);

/// <summary>
/// Everything the slice remembers. In memory, single tenant, deliberately small.
/// </summary>
public sealed class BrokerState
{
    /// <summary>
    /// The clients the broker publishes for anyone to use against pre-production, so an
    /// existing configuration reaches StubID by changing the authority alone.
    /// </summary>
    /// <remarks>
    /// All three sit in one organisation. Whether the broker groups its own published clients
    /// that way is unobserved; one organisation is the arrangement a company integrating
    /// several applications actually has, and it is the one that exercises the shared subject.
    /// </remarks>
    public IReadOnlyDictionary<string, Client> Clients { get; } =
        new Dictionary<string, Client>(StringComparer.Ordinal)
        {
            ["0a775a87-878c-4b83-abe3-ee29c720c3e7"] =
                new("0a775a87-878c-4b83-abe3-ee29c720c3e7", ["code"], "published-test-clients"),
            ["c0beb4dc-69d1-4316-8167-2d0a62816103"] =
                new("c0beb4dc-69d1-4316-8167-2d0a62816103", ["id_token code"], "published-test-clients"),
            ["93ed8e0d-93ad-405c-b1ac-8bf13d484941"] =
                new("93ed8e0d-93ad-405c-b1ac-8bf13d484941", ["id_token"], "published-test-clients"),
        };

    /// <summary>
    /// Whether a client may ask for this response type. The comparison ignores order, as the
    /// broker's does: its own hybrid client declares "id_token code" while a client library
    /// sends "code id_token".
    /// </summary>
    public bool Allows(string clientId, string responseType) =>
        Clients.TryGetValue(clientId, out var client)
        && client.ResponseTypes.Any(allowed => allowed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(responseType.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>The organisation a client belongs to, which is what a subject is scoped to.</summary>
    public string OrganisationOf(string clientId) =>
        Clients.TryGetValue(clientId, out var client) ? client.Organisation : clientId;

    private readonly ConcurrentDictionary<string, AuthorizationRequest> _pushed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedAccessToken> _accessTokens = new(StringComparer.Ordinal);

/// <summary>A registered client, and the organisation it belongs to.</summary>
/// <param name="Organisation">
/// What the subject is scoped to. Two clients of one organisation receive the same subject
/// for the same person, which is what the id_token calls org_mapped.
/// </param>
public sealed record Client(string ClientId, string[] ResponseTypes, string Organisation);

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
        _codes[code] = new IssuedCode(
            request, citizen, now,
            SessionId: Guid.NewGuid().ToString(),
            TransactionId: Guid.NewGuid().ToString(),
            IdpTransactionId: Guid.NewGuid().ToString());
        return code;
    }

    /// <summary>
    /// Reads an issued code without consuming it, for composing the front-channel token that
    /// accompanies it.
    /// </summary>
    public IssuedCode? PeekCode(string code) => _codes.GetValueOrDefault(code);

    /// <summary>Codes are single use: redeeming one removes it.</summary>
    public IssuedCode? RedeemCode(string code) =>
        _codes.TryRemove(code, out var issued) ? issued : null;

    public string IssueAccessToken(IssuedCode code)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _accessTokens[token] = new IssuedAccessToken(
            code.Request.ClientId, code.Citizen, code.Request.Scope,
            code.SessionId, code.IdpTransactionId, code.AuthenticatedAt);
        return token;
    }

    public IssuedAccessToken? ReadAccessToken(string token) =>
        _accessTokens.TryGetValue(token, out var issued) ? issued : null;
}
