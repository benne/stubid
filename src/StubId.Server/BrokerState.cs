using System.Collections.Concurrent;
using System.Security.Cryptography;
using StubId.Abstractions;
using StubId.Server.Sessions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>A parked authorization request, whether it arrived by PAR or by redirect.</summary>
/// <param name="MitIdParameters">
/// The <c>mitid</c> section of idp_params, decoded but not inspected. It rides here rather than
/// on the session because the claims it feeds are written at the token endpoint, which can reach
/// an AuthorizationRequest and cannot reach a session: a pushed request never passes the place
/// sessions are created, and the identifiers on either side are unrelated.
/// <para>
/// The mitid section alone. Every recording that carries idp_params carries it under that key,
/// and a login through mitid_erhverv produces private-identity claims anyway - what a business
/// identity would put here is unobserved and is its own milestone.
/// </para>
/// <para>
/// A reference type on a record, which makes this record's generated equality compare it by
/// reference: two requests built from identical queries are no longer equal. Nothing compares
/// AuthorizationRequest today - it is stored as a dictionary value and never as a key - so this
/// costs nothing, but a future Distinct or Contains over these would quietly stop working.
/// </para>
/// </param>
public sealed record AuthorizationRequest(
    string ClientId,
    string RedirectUri,
    string ResponseType,
    string ResponseMode,
    string Scope,
    string? State,
    string? Nonce,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    IReadOnlyDictionary<string, string>? MitIdParameters = null)
{
    /// <summary>
    /// The MitID-native text shown while the user approves, base64 as the client sent it.
    /// </summary>
    /// <remarks>
    /// Distinct from a transaction text: this one is MitID's own, is limited to 130 characters,
    /// and is what the MitID app displays. The broker echoes it whole to the transaction token
    /// and to the userinfo endpoint, with no type and no digest beside it.
    /// </remarks>
    public string? ReferenceText =>
        MitIdParameters?.GetValueOrDefault("reference_text") is { Length: > 0 } text ? text : null;
}

/// <summary>An issued authorization code and everything the token endpoint needs to redeem it.</summary>
/// <param name="TransactionId">The broker's own identifier for the exchange.</param>
/// <param name="IdpTransactionId">
/// The identity provider's, which differs from it. Both are sent, and the userinfo response
/// reports the second under mitid.transaction_id.
/// </param>
/// <param name="ClientIp">
/// Who authorized, which the transaction token reports as transaction_client_ip. Taken where
/// the browser arrives rather than where the token is collected: the token endpoint is called
/// by the application's back end, and its address is not the one that signed anything.
/// </param>
public sealed record IssuedCode(
    AuthorizationRequest Request,
    Citizen Citizen,
    DateTimeOffset AuthenticatedAt,
    string SessionId,
    string TransactionId,
    string IdpTransactionId,
    string ClientIp);

/// <summary>An access token, the identity behind it, and the client that obtained it.</summary>
/// <remarks>
/// The client matters: the subject is scoped to the receiving organisation, so userinfo has
/// to answer with the same subject the id_token carried, and that depends on who is asking.
/// </remarks>
/// <param name="ReferenceText">
/// Carried separately because this record holds no AuthorizationRequest, and the userinfo
/// endpoint answers from an access token alone. The recorded userinfo response returns a
/// reference text whole, in the same slot the transaction token puts it.
/// </param>
public sealed record IssuedAccessToken(
    string ClientId,
    Citizen Citizen,
    string Scope,
    string SessionId,
    string IdpTransactionId,
    DateTimeOffset AuthenticatedAt,
    string? ReferenceText = null);

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
    /// Whether an id_token_hint is shaped like a token that carries a session, which is what
    /// decides if a post-logout redirect is honoured.
    /// </summary>
    /// <remarks>
    /// It reads the token; it does not verify it. Any three-part token whose payload has a
    /// sid gets through, including one this instance never issued. That is deliberate and it
    /// is the same trade as not checking client secrets: a stub that demanded a token it had
    /// signed itself would refuse a hint a test had built by hand, which is the more likely
    /// case here. The broker refuses those; StubID does not, and says so in its divergences.
    /// </remarks>
    public bool EndsSession(string idTokenHint)
    {
        var parts = idTokenHint.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            using var payload = System.Text.Json.JsonDocument.Parse(
                System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));

            return payload.RootElement.TryGetProperty("sid", out var sid)
                && sid.GetString() is { Length: > 0 };
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

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

    public string IssueCode(
        AuthorizationRequest request, Citizen citizen, DateTimeOffset now, string clientIp)
    {
        var code = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new IssuedCode(
            request, citizen, now,
            SessionId: Guid.NewGuid().ToString(),
            TransactionId: Guid.NewGuid().ToString(),
            IdpTransactionId: Guid.NewGuid().ToString(),
            ClientIp: clientIp);
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
            code.SessionId, code.IdpTransactionId, code.AuthenticatedAt,
            code.Request.ReferenceText);
        return token;
    }

    public IssuedAccessToken? ReadAccessToken(string token) =>
        _accessTokens.TryGetValue(token, out var issued) ? issued : null;

    /// <summary>
    /// Ends a session, so every token issued from it stops working. The broker's own
    /// documentation recommends this over sending the browser to the end-session endpoint,
    /// and a test that wants to prove its cleanup runs needs the tokens to actually die.
    /// </summary>
    public void EndSession(string sessionId)
    {
        foreach (var (token, issued) in _accessTokens)
        {
            if (issued.SessionId == sessionId)
            {
                _accessTokens.TryRemove(token, out _);
            }
        }
    }
}
