using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StubId.Abstractions;
using StubId.Wire;

namespace StubId.Server.Signicat;

/// <summary>
/// Writes a key set in the shape this broker publishes.
/// </summary>
/// <remarks>
/// Not the first broker's writer with different arguments. That one emits <c>x5t</c> and an
/// <c>x5c</c> chain and no <c>alg</c>, because its keys are certificates and it publishes them;
/// this broker publishes bare RSA keys, in the order <c>kty, use, kid, e, n, alg</c>, with the
/// algorithm named and no chain at all. A client that selects a key by algorithm, or one that
/// walks a chain, behaves differently against the two, which is the whole reason each is written
/// out by hand rather than handed to a JOSE library.
/// </remarks>
internal static class SignicatKeySet
{
    /// <summary>
    /// The same keys on every request, where the broker serves a different subset each time.
    /// </summary>
    /// <remarks>
    /// Measured: three fetches a second apart returned 26, 24 and 22 keys. A client that caches a
    /// key set and trusts it fails against this broker some of the time and passes here always,
    /// which is exactly the false pass a stub is meant not to give. Reproducing it would mean
    /// inventing keys nothing signs with, so the divergence is recorded instead of imitated.
    /// </remarks>
    [Fidelity(FidelityTier.Shape, FidelityProvenance.Divergent,
        Reason = "docs/brokers/signicat/divergences.md#the-key-set")]
    public static string Write(Keys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var buffer = new MemoryStream();

        // The default encoder escapes a plus sign, and standard base64 is full of them.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteStartArray("keys");

            Write(json, keys.TokenSigning, "sig", "RS256", "signing-key-");
            Write(json, keys.Ring.Keys.First(key => key.Use == KeyUse.Encryption),
                "enc", "RSA-OAEP", "encryption-key-");

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter json, SigningKey key, string use, string algorithm, string prefix)
    {
        var parameters = key.PublicKey.ExportParameters(includePrivateParameters: false);

        json.WriteStartObject();
        json.WriteString("kty", "RSA");
        json.WriteString("use", use);
        json.WriteString("kid", Kid(key, prefix));
        json.WriteString("e", Base64Url.Encode(parameters.Exponent!));
        json.WriteString("n", Base64Url.Encode(parameters.Modulus!));
        json.WriteString("alg", algorithm);
        json.WriteEndObject();
    }

    /// <summary>
    /// A key id in this broker's form: a role, then thirty-two lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Derived from the certificate's thumbprint rather than generated, so it survives a restart
    /// for the same reason the key does. The broker's own suffix looks like a GUID rather than a
    /// thumbprint; imitating that would be inventing a value the recording does not promise, where
    /// the shape is what a client reads.
    /// </remarks>
    private static string Kid(SigningKey key, string prefix) =>
        prefix + key.Kid[..32].ToLowerInvariant();
}
