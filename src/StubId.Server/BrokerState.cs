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

    /// <summary>
    /// The transaction text, base64 as the client sent it. The broker echoes it undecoded.
    /// </summary>
    public string? TransactionText =>
        MitIdParameters?.GetValueOrDefault("transaction_text") is { Length: > 0 } text ? text : null;

    /// <summary>What the client said the text is. <c>text</c> and <c>html</c> are the documented pair.</summary>
    public string? TransactionTextType =>
        MitIdParameters?.GetValueOrDefault("transaction_text_type") is { Length: > 0 } type ? type : null;

    /// <summary>
    /// The digest the broker publishes beside the text: base64 of SHA-256 over the
    /// <em>decoded</em> bytes, standard alphabet and padded. Null when there is nothing to hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed here rather than at either writer. The transaction token and the userinfo
    /// endpoint both carry it, they run at different times, and a decode in each is how the two
    /// drift apart - so there is one decoder, one rule for what it refuses, and both writers
    /// inherit it. It is also the only guard: a FormatException anywhere on this path leaves the
    /// pipeline as an empty 500, which is the one answer the broker never gives.
    /// </para>
    /// <para>
    /// Not <see cref="HashClaims"/>. That helper is for at_hash and c_hash - the left half of the
    /// digest, base64url, over the ASCII of the value as sent - and it is wrong here three
    /// separate ways.
    /// </para>
    /// </remarks>
    public string? TransactionTextSha256 =>
        DecodedTransactionText is { Length: > 0 } bytes
            ? Convert.ToBase64String(SHA256.HashData(bytes))
            : null;

    /// <summary>The bytes behind the text, or null when the value is not something this can decode.</summary>
    /// <remarks>
    /// <para>
    /// Both alphabets are accepted. The recorded text sits in the intersection of standard and
    /// URL-safe base64 - no <c>+</c>, <c>/</c>, <c>-</c> or <c>_</c>, and a length that is a
    /// multiple of four - so no recording says which the broker parses, and this repository
    /// already reads that same value with a base64url decoder in its fixture contract test.
    /// Accepting both is the reading that refuses fewest of the values a client might send.
    /// </para>
    /// <para>
    /// Whitespace is refused rather than skipped, which is what
    /// <see cref="Convert.FromBase64String(string)"/> would do with it. Skipping it changes the
    /// answer without saying so: a value with characters removed still decodes, to different
    /// bytes, so the digest comes back looking right and matching nothing the client holds. A
    /// missing digest is the more useful failure.
    /// </para>
    /// </remarks>
    public byte[]? DecodedTransactionText => DecodeTransactionText(TransactionText);

    /// <summary>
    /// The one decoder. Public because the login page needs the same answer this does, and two
    /// readings of one value is how a page and a token come to disagree about what was signed.
    /// </summary>
    public static byte[]? DecodeTransactionText(string? value)
    {
        if (value is null || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');

        var buffer = new byte[standard.Length];

        return Convert.TryFromBase64String(standard, buffer, out var written) && written > 0
            ? buffer[..written]
            : null;
    }
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
/// <param name="LoginId">
/// The login this came from, which is not <paramref name="SessionId" />. That one is the broker's
/// own sid, minted here and put into the tokens; this one is the <c>AuthSession</c> a browser
/// parked, and it is the only thing that ties an issued artefact back to a login somebody can
/// look at.
/// </param>
public sealed record IssuedCode(
    AuthorizationRequest Request,
    Citizen Citizen,
    DateTimeOffset AuthenticatedAt,
    string SessionId,
    string LoginId,
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
/// <param name="TransactionTextType">
/// Carried for the same reason, and this is the half of the transaction text userinfo sends.
/// </param>
/// <param name="TransactionTextSha256">
/// The other half. The text itself is deliberately not here: CAP-031's userinfo response hands
/// over the digest and the type and withholds the text they describe, which is the opposite of
/// what the same endpoint does with a reference text.
/// </param>
public sealed record IssuedAccessToken(
    string ClientId,
    Citizen Citizen,
    string Scope,
    string SessionId,
    string LoginId,
    string IdpTransactionId,
    DateTimeOffset AuthenticatedAt,
    string? ReferenceText = null,
    string? TransactionTextType = null,
    string? TransactionTextSha256 = null);

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

    /// <summary>A pushed request and the moment it stops being redeemable.</summary>
    private sealed record PushedRequest(AuthorizationRequest Request, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, PushedRequest> _pushed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedAccessToken> _accessTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// What has been handed out, newest first, with none of the values in it.
    /// </summary>
    /// <remarks>
    /// These three dictionaries were unreadable from outside, which made "why did my client get a
    /// token it should not have" a question only a debugger could answer. The keys are the
    /// credentials, so nothing here reads one: see <see cref="IssuedArtefact" />.
    /// </remarks>
    public IReadOnlyList<IssuedArtefact> Issued() =>
    [
        .. _pushed.Values
            .Select(pushed => new IssuedArtefact(
                "pushed request",
                pushed.Request.ClientId,
                CitizenId: null,
                LoginId: null,
                AuthenticatedAt: null,
                pushed.Expires,
                pushed.Request.Scope))
            .Concat(_codes.Values.Select(code => new IssuedArtefact(
                "code",
                code.Request.ClientId,
                code.Citizen.Id,
                code.LoginId,
                code.AuthenticatedAt,
                Expires: null,
                code.Request.Scope)))
            .Concat(_accessTokens.Values.Select(token => new IssuedArtefact(
                "access token",
                token.ClientId,
                token.Citizen.Id,
                token.LoginId,
                token.AuthenticatedAt,
                Expires: null,
                token.Scope)))
            .OrderByDescending(artefact => artefact.AuthenticatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(artefact => artefact.Kind, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Drops everything handed out so far.
    /// </summary>
    /// <remarks>
    /// A reset used to clear the sessions and leave these standing, which meant a code from
    /// before it could still be redeemed and the counts on a page survived the button that
    /// claimed to clear them. Nothing here is setup a suite builds once - unlike the citizens,
    /// which a reset keeps on purpose.
    /// </remarks>
    public void Forget()
    {
        _pushed.Clear();
        _codes.Clear();
        _accessTokens.Clear();
    }

/// <summary>
/// One thing this instance has handed out, described without handing it out again.
/// </summary>
/// <remarks>
/// The value is deliberately absent, and there is not even a prefix of it. A code and an access
/// token are the keys of the dictionaries they live in, and both are credentials: a page that
/// printed one would turn "see what this instance issued" into "issue yourself a token as
/// anybody", on a surface that asks nobody who they are. What lines an entry up against a login
/// is its session id, which is already public.
/// </remarks>
/// <param name="Kind">A pushed request, a code, or an access token.</param>
public sealed record IssuedArtefact(
    string Kind,
    string ClientId,
    string? CitizenId,
    string? LoginId,
    DateTimeOffset? AuthenticatedAt,
    DateTimeOffset? Expires,
    string? Scope);

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

    /// <summary>
    /// How long a pushed request may be left unredeemed. Measured rather than chosen: the
    /// broker answers a good push with <c>expires_in: 600</c>
    /// (docs/research/signed-requests.md).
    /// </summary>
    /// <remarks>
    /// The same constant the PAR endpoint advertises, so the number a client is told and the
    /// number it is held to cannot drift apart. They were separate until this was enforced, and
    /// the advertised one was the only one that existed.
    /// </remarks>
    public const int PushedRequestLifetimeSeconds = 600;

    /// <summary>
    /// Pushes a request and returns the reference that redeems it, once, within ten minutes.
    /// </summary>
    /// <remarks>
    /// Expiry is checked when a reference is redeemed rather than swept by a timer, which is the
    /// rule sessions already follow: it stays honest under a controllable clock, so a test moves
    /// time and sees the effect on its next request with nothing to wait for. The one sweep is
    /// here, over entries whose time has already passed, so a push nobody ever redeems is not a
    /// leak for the life of the instance.
    /// </remarks>
    /// <remarks>
    /// Nothing asserts that sweep directly, and nothing can from outside: a stale reference is
    /// refused on its own merits whether or not anything removed it first. It is here for the
    /// memory rather than for the answer.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "docs/research/signed-requests.md")]
    [Fidelity(FidelityTier.Exact, FidelityProvenance.Assumed,
        AwaitingCapture = "That the lifetime is 600 seconds is measured; that the broker enforces "
                          + "it is not. Reaching that needs a push left for ten minutes and then "
                          + "redeemed, which no capture step waits for. RFC 9126 2.2 says a "
                          + "request_uri expires, and the authorize endpoint already answered "
                          + "'Unknown or expired request_uri' for a reference it could not find.")]
    public string PushRequest(AuthorizationRequest request, DateTimeOffset now)
    {
        var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pushed[reference] = new PushedRequest(request, now.AddSeconds(PushedRequestLifetimeSeconds));

        foreach (var (stale, pushed) in _pushed)
        {
            if (now >= pushed.Expires)
            {
                _pushed.TryRemove(stale, out _);
            }
        }

        return $"urn:ietf:params:oauth:request_uri:{reference}";
    }

    /// <summary>
    /// Spends a reference. Null when it is unknown, already spent, or out of time - which the
    /// authorize endpoint answers with one error for all three, as it always has.
    /// </summary>
    public AuthorizationRequest? RedeemPushedRequest(string requestUri, DateTimeOffset now)
    {
        var reference = requestUri.Split(':').LastOrDefault() ?? "";

        // Removed either way. A reference past its time is spent by being tried, so a second
        // attempt cannot tell a stale one from a used one - and neither can the client.
        return _pushed.TryRemove(reference, out var pushed) && now < pushed.Expires
            ? pushed.Request
            : null;
    }

    public string IssueCode(
        AuthorizationRequest request,
        Citizen citizen,
        DateTimeOffset now,
        string clientIp,
        string loginId)
    {
        var code = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new IssuedCode(
            request, citizen, now,
            SessionId: Guid.NewGuid().ToString(),
            LoginId: loginId,
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
            code.SessionId, code.LoginId, code.IdpTransactionId, code.AuthenticatedAt,
            code.Request.ReferenceText,
            code.Request.TransactionTextType,
            code.Request.TransactionTextSha256);
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
