using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Packs an authorize request into a JWT signed with the client secret, and takes it back out
/// again before anything is written to disk.
/// </summary>
/// <remarks>
/// <para>
/// The broker limits the transaction-text flow to signed requests, so a sitting that wants the
/// transaction-text claims has to send one. What it accepts was measured rather than assumed,
/// and the measurement is in docs/research/signed-requests.md: HS256 over the client secret,
/// on both the published open client and a private one.
/// </para>
/// <para>
/// StubId.Wire's JwsWriter is not reusable here. It signs with an X509Certificate2 private key
/// and hard-codes RS256, and the harness deliberately does not depend on the server it records
/// against.
/// </para>
/// </remarks>
public static partial class RequestObject
{
    /// <summary>The placeholder a recorded request object is replaced by.</summary>
    public const string Placeholder = "{{REQUEST_OBJECT}}";

    /// <summary>
    /// How long the object is valid for. Long enough that an operator reading the step first
    /// does not have to hurry, short enough that a URL left in a terminal stops working.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Builds the compact JWS. Every authorize parameter becomes a claim, which is the point:
    /// the query then carries only what identifies the request, and the broker reads the rest
    /// from in here.
    /// </summary>
    public static string Build(
        IReadOnlyDictionary<string, string> parameters,
        string clientId,
        string authority,
        string secret,
        DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            claims[key] = value;
        }

        claims["iss"] = clientId;
        claims["aud"] = authority;

        // exp is not optional, and its absence is the most expensive way to get this wrong.
        // A request object without it is refused with bytes identical to a forged signature -
        // invalid_request_object, "Invalid JWT request" - so a probe that omits it fails every
        // case including its own negative control and reads as "signed requests do not work
        // here". That is exactly what happened before this was measured; see
        // docs/research/signed-requests.md. iat and nbf are not required and are sent because
        // a request object without them is unusual enough to invite a second question.
        claims["exp"] = issued.Add(Lifetime).ToUnixTimeSeconds();
        claims["iat"] = issued.ToUnixTimeSeconds();
        claims["nbf"] = issued.ToUnixTimeSeconds();
        claims["jti"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

        var header = Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        });

        var payload = Encode(claims);
        var signingInput = $"{header}.{payload}";

        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>
    /// Replaces a request object in a recorded URL with a placeholder, and hands back what it
    /// replaced so the header and payload can be written beside the exchange.
    /// </summary>
    /// <remarks>
    /// The same reasoning TokenFixtures gives for response bodies, for the same reason: a JWS
    /// cannot be scrubbed in place, because changing a byte inside it invalidates the
    /// signature and re-signing produces bytes nobody sent. It also keeps a compact token out
    /// of a fixture, which the guard rejects and which has reached one twice already.
    /// </remarks>
    public static (string Url, ExtractedToken? Object) StripFrom(string url)
    {
        var match = RequestParameter().Match(url);
        if (!match.Success)
        {
            return (url, null);
        }

        var compact = Uri.UnescapeDataString(match.Groups["jwt"].Value);
        var parts = compact.Split('.');
        if (parts.Length != 3)
        {
            return (url, null);
        }

        var extracted = new ExtractedToken(
            Placeholder,
            Decode(parts[0]),
            Decode(parts[1]),
            Member(parts[0], "alg"),
            Member(parts[0], "kid"),
            [.. parts.Select(p => p.Length)],

            // Ours, not the broker's. There is no published key to check it against, and
            // "verified" would mean no more than that we can still compute our own HMAC.
            null);

        return (url.Remove(match.Groups["jwt"].Index, match.Groups["jwt"].Length)
                   .Insert(match.Groups["jwt"].Index, Placeholder),
                extracted);
    }

    private static string Encode(IReadOnlyDictionary<string, object> members) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(members));

    private static string Decode(string segment)
    {
        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segment));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static string? Member(string headerSegment, string name)
    {
        try
        {
            using var header = JsonDocument.Parse(Decode(headerSegment));
            return header.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Matches the request parameter's value. Percent-encoding is allowed for, because the URL
    /// is recorded as it was sent rather than as it was assembled.
    /// </summary>
    [GeneratedRegex(@"[?&]request=(?<jwt>[A-Za-z0-9_\-]+(?:\.|%2[Ee])[A-Za-z0-9_\-]+(?:\.|%2[Ee])[A-Za-z0-9_\-]+)")]
    private static partial Regex RequestParameter();
}
