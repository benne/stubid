using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StubId.Abstractions;

namespace StubId.Wire;

/// <summary>
/// Writes a signed JWT from an ordered list of claims.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not built on a token library. Handing a descriptor to one means it decides
/// which claims exist, in what order, and with what JSON type: it will add an <c>nbf</c>
/// because tokens usually have one, turn a numeric-looking string into a number, and sort
/// members as it sees fit. Every one of those is a difference a client can observe, and this
/// project exists to not have them.
/// </para>
/// <para>
/// So the claims given are the claims written, in the order given, with the types given.
/// Nothing is added here — not even <c>iat</c>. Deciding a token's contents is the profile's
/// job, and it is the only place that knows what the broker actually sends.
/// </para>
/// </remarks>
public sealed class JwsWriter
{
    /// <summary>
    /// Header member order confirmed against a recorded login: alg, kid, typ. The type is a
    /// parameter because the same broker sends JWT for an id_token and at+jwt for a userinfo
    /// token, in one response.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp-session/CAP-024/token/id_token.header.json")]
    public string Sign(IReadOnlyList<JsonClaim> claims, SigningKey key, string type = "JWT")
    {
        var header = Encode(
        [
            JsonClaim.String("alg", "RS256"),
            JsonClaim.String("kid", key.Kid),
            JsonClaim.String("typ", type),
        ]);

        var payload = Encode(claims);
        var signingInput = $"{header}.{payload}";

        var signature = key.PrivateKey.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }

    private static string Encode(IReadOnlyList<JsonClaim> claims)
    {
        var duplicate = claims.GroupBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Claim '{duplicate.Key}' appears more than once. A JSON object with a repeated "
                + "member is read differently by different parsers, so it is never what was meant.",
                nameof(claims));
        }

        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            foreach (var claim in claims)
            {
                json.WritePropertyName(claim.Name);
                using var value = JsonDocument.Parse(claim.RawJson);
                value.RootElement.WriteTo(json);
            }

            json.WriteEndObject();
        }

        return Base64Url.Encode(buffer.ToArray());
    }
}
