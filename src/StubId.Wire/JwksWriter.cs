using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StubId.Abstractions;

namespace StubId.Wire;

/// <summary>
/// Writes a JWKS in the shape the broker publishes.
/// </summary>
/// <remarks>
/// Written member by member rather than by serialising a key object. Every JOSE library
/// helpfully adds an <c>alg</c> member; the broker publishes none, and a client that selects
/// keys by algorithm behaves differently depending on whether it is there. Emitting the
/// document by hand is what keeps our output honest.
/// </remarks>
[Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
    Evidence = "fixtures/neb/pp/CAP-002")]
public static class JwksWriter
{
    /// <summary>
    /// Member order as published: kty, use, kid, x5t, e, n, x5c. No alg, and x5c always
    /// holds exactly one certificate.
    /// </summary>
    public static string Write(IEnumerable<SigningKey> keys)
    {
        var buffer = new MemoryStream();
        // The default encoder escapes '+' as \u002B, which standard base64 is full of. The
        // recording carries the character itself, and this document is compared byte for byte.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteStartArray("keys");

            foreach (var key in keys)
            {
                var parameters = key.PublicKey.ExportParameters(includePrivateParameters: false);

                json.WriteStartObject();
                json.WriteString("kty", "RSA");
                json.WriteString("use", key.UseValue);
                json.WriteString("kid", key.Kid);
                json.WriteString("x5t", key.X5t);
                json.WriteString("e", Base64Url.Encode(parameters.Exponent!));
                json.WriteString("n", Base64Url.Encode(parameters.Modulus!));
                json.WriteStartArray("x5c");
                json.WriteStringValue(key.X5c);
                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
