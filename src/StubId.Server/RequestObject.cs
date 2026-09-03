using System.Text.Json;
using StubId.Abstractions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// The <c>request</c> parameter: an authorization request packed into a JWT, unpacked again.
/// </summary>
/// <remarks>
/// <para>
/// Discovery advertises it, and the broker really does read it: CAP-031's authorize URL carried
/// <c>client_id</c>, <c>response_type</c> and <c>request</c> and nothing else - no scope, no
/// redirect_uri, no nonce, no PKCE - and the login it started came back to a redirect URI the
/// query never named, carrying the state the object did. So the parameters came out of the
/// object rather than out of a query that on its own is not an authorization request.
/// </para>
/// <para>
/// The signature is not checked, for the same reason a client secret is not
/// (<see href="../../docs/brokers/neb/divergences.md#request-objects">divergences</see>): StubID
/// holds no secret to check it against, and refusing an object a test assembled by hand would
/// fail a test that works against the broker. What is checked is that the thing can be read at
/// all, which is the difference between a stub and a hole.
/// </para>
/// </remarks>
[Fidelity(FidelityTier.Shape, FidelityProvenance.Divergent,
    Reason = "docs/brokers/neb/divergences.md#request-objects")]
public static class RequestObject
{
    /// <summary>
    /// The broker's code and sentence for an object it will not read, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CAP-046 is the recording, and it is of the case this code actually meets: a
    /// <c>request</c> parameter that is not a JWS at all. The broker answers 400 with these
    /// bytes and nothing else, which also settles the status - until that recording it was an
    /// inference from RFC 9126 and from CAP-019's other refusal.
    /// </para>
    /// <para>
    /// Three further causes earn the identical answer and are measured rather than recorded: a
    /// flipped signature byte, a signature from a random key, and a missing <c>exp</c>, on two
    /// clients and two runs. They stay in docs/research/signed-requests.md because recording one
    /// means committing a request object signed HS256 with the client secret, and a compact JWS
    /// like that is a known-plaintext HMAC tag over the secret that signed it - an offline
    /// oracle, in a public repository. The manual sitting takes the same view: CAP-031 records a
    /// request object's algorithm and segment lengths and never its signature.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-046, docs/research/signed-requests.md")]
    public const string Fault = "invalid_request_object";

    /// <summary>The sentence beside it. The broker sends one here; at the token endpoint it does not.</summary>
    public const string FaultDescription = "Invalid JWT request";

    /// <summary>The JWT's own furniture, dropped rather than merged.</summary>
    /// <remarks>
    /// The harness adds these six separately from the parameters it packs, which is the same
    /// division RFC 9101 draws: the object's <em>request parameters</em> are what the endpoint
    /// takes out of it. Dropping them keeps a session's parameter view free of furniture no
    /// client sent.
    /// </remarks>
    private static readonly string[] Registered = ["iss", "aud", "exp", "iat", "nbf", "jti"];

    /// <summary>
    /// Merges a request object's claims over the parameters it arrived with. False when there is
    /// an object and it cannot be read, which is the one case either endpoint refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object wins where both carry the same name. That is what OpenID Connect Core 6.1
    /// says and it is not observable from CAP-031, whose query and object agree on both names
    /// they share.
    /// </para>
    /// <para>
    /// <c>exp</c> is required and is the expensive one. Its absence earns bytes identical to a
    /// forged signature, so a probe that omits it fails every case it tries including its own
    /// negative control, and reads as a clean "signed requests do not work here". Presence is
    /// all that is checked: this reads the object rather than validating it, and expiring one
    /// while ignoring its signature would be a strange half of a check to keep.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-031/callback/meta.json, "
                   + "fixtures/neb/pp-session/CAP-031/callback/request_object.payload.json")]
    public static bool TryMerge(IDictionary<string, string> parameters)
    {
        // An empty value is treated as no object at all, which is what every other optional
        // parameter here does with one. Unmeasured: no probe sent request= with nothing after it.
        if (!parameters.TryGetValue("request", out var compact) || compact.Length == 0)
        {
            return true;
        }

        var segments = compact.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        // Collected first and applied after. A claim that cannot be materialised throws part of
        // the way through, and half a request object merged over the query is worse than none.
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.Decode(segments[1]));

            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("exp", out _))
            {
                return false;
            }

            foreach (var claim in payload.RootElement.EnumerateObject())
            {
                if (Registered.Contains(claim.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                // idp_params travels as a JSON string holding JSON, so it has to come back out
                // as the string it was rather than as re-serialised JSON: what reads it next
                // parses it again. Anything not a string is rendered, which is how a number or
                // a boolean written where a parameter was expected reaches the same place a
                // query would have put it.
                claims[claim.Name] = claim.Value.ValueKind == JsonValueKind.String
                    ? claim.Value.GetString()!
                    : claim.Value.ToString();
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            // FormatException is a payload segment that is not base64url. JsonException is one
            // that is and holds no JSON. InvalidOperationException is a string that parsed and
            // cannot be read - an unpaired UTF-16 surrogate escape is accepted by the parser and
            // throws only when the string is materialised. All three are the same answer: there
            // is an object, and it cannot be read.
            return false;
        }

        foreach (var (name, value) in claims)
        {
            parameters[name] = value;
        }

        return true;
    }
}
