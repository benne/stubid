using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StubId.CaptureHarness;

/// <summary>A signed token pulled out of a response so it can be stored honestly.</summary>
/// <param name="Placeholder">What replaces the compact form in the response body.</param>
/// <param name="Header">The decoded header bytes, exactly as they were.</param>
/// <param name="Payload">The decoded payload bytes, exactly as they were.</param>
/// <param name="Algorithm">From the header.</param>
/// <param name="Kid">From the header, so the key that signed it can be named.</param>
/// <param name="SegmentLengths">The real lengths, which a placeholder would otherwise lose.</param>
/// <param name="SignatureVerified">
/// Whether the signature checked out against the broker's published key at the moment of
/// recording. That answer cannot be recovered later: the broker rotates its keys, and the
/// transaction-signing certificate already rotated once in May 2026.
/// </param>
public sealed record ExtractedToken(
    string Placeholder,
    string Header,
    string Payload,
    string? Algorithm,
    string? Kid,
    int[] SegmentLengths,
    bool? SignatureVerified);

/// <summary>
/// Splits signed tokens out of a recorded response.
/// </summary>
/// <remarks>
/// <para>
/// A token cannot be scrubbed in place: changing a byte inside a JWS invalidates its
/// signature, and re-signing it produces bytes the broker never sent. So the response body
/// keeps its shape with the compact token replaced by a placeholder, which preserves member
/// order and position, and the decoded header and payload are written beside it verbatim.
/// Member order inside the token is the whole evidence for what the broker sends.
/// </para>
/// <para>
/// A token re-signed with the fixture key is a derived artefact for tests that need a whole
/// parseable document. It is never presented as what was recorded.
/// </para>
/// </remarks>
public static class TokenFixtures
{
    /// <summary>Names the well-known tokens so a placeholder says what it replaced.</summary>
    private static readonly string[] Known =
        ["id_token", "access_token", "userinfo_token", "transaction_token", "refresh_token"];

    public static (string Body, IReadOnlyDictionary<string, ExtractedToken> Tokens) Extract(
        string body, Func<string, bool>? verify = null)
    {
        var extracted = new Dictionary<string, ExtractedToken>(StringComparer.Ordinal);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON. A front-channel response arrives as name=value lines, and the
            // implicit flow puts an id_token in one of them.
            return ExtractFromFields(body, extracted, verify);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (body, extracted);
            }

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = member.Value.GetString()!;
                if (!Known.Contains(member.Name) || !LooksSigned(value))
                {
                    continue;
                }

                extracted[member.Name] = Describe(member.Name, value, verify);

                // A byte-level splice: the surrounding document keeps its exact shape.
                body = body.Replace(value, extracted[member.Name].Placeholder, StringComparison.Ordinal);
            }
        }

        return (body, extracted);
    }

    /// <summary>
    /// Handles a name=value body, which is what a front-channel response is. The implicit
    /// flow returns its id_token this way, and a JSON-only extractor left it in the fixture.
    /// </summary>
    private static (string Body, IReadOnlyDictionary<string, ExtractedToken> Tokens) ExtractFromFields(
        string body, Dictionary<string, ExtractedToken> extracted, Func<string, bool>? verify)
    {
        foreach (var line in body.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();

            if (!Known.Contains(name) || !LooksSigned(value))
            {
                continue;
            }

            extracted[name] = Describe(name, value, verify);
            body = body.Replace(value, extracted[name].Placeholder, StringComparison.Ordinal);
        }

        return (body, extracted);
    }

    private static ExtractedToken Describe(string name, string value, Func<string, bool>? verify)
    {
        var parts = value.Split('.');

        return new ExtractedToken(
            $"{{{{{name.ToUpperInvariant()}}}}}",
            Decode(parts[0]),
            Decode(parts[1]),
            Member(parts[0], "alg"),
            Member(parts[0], "kid"),
            [.. parts.Select(p => p.Length)],
            verify?.Invoke(value));
    }

    private static bool LooksSigned(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts[0].Length > 8 && parts[1].Length > 8;
    }

    private static string Decode(string segment)
    {
        try
        {
            return Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(segment));
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
    /// The subject of the certificate a kid resolves to in the published key set.
    /// </summary>
    /// <remarks>
    /// A thumbprint says nothing on its own. The certificate subjects are what separate the
    /// transaction-signing key from the ordinary token-signing one, and a fixture that records
    /// only the kid leaves that to be looked up against a key set which will have rotated.
    /// </remarks>
    public static string? SubjectFor(string? kid, string jwksJson)
    {
        if (kid is null)
        {
            return null;
        }

        try
        {
            using var jwks = JsonDocument.Parse(jwksJson);
            var key = jwks.RootElement.GetProperty("keys").EnumerateArray()
                .FirstOrDefault(k => k.TryGetProperty("kid", out var candidate)
                                     && candidate.GetString() == kid);

            if (key.ValueKind != JsonValueKind.Object
                || !key.TryGetProperty("x5c", out var chain)
                || chain.GetArrayLength() == 0)
            {
                return null;
            }

            using var certificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(chain[0].GetString()!));

            return certificate.Subject;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException
                                       or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a token against the broker's published key set, by the kid in its header. Run
    /// at capture time because it cannot be run afterwards.
    /// </summary>
    public static bool Verify(string compact, string jwksJson)
    {
        try
        {
            var parts = compact.Split('.');
            var kid = Member(parts[0], "kid");

            using var jwks = JsonDocument.Parse(jwksJson);
            var key = jwks.RootElement.GetProperty("keys").EnumerateArray()
                .FirstOrDefault(k => k.TryGetProperty("kid", out var candidate)
                                     && candidate.GetString() == kid);

            if (key.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = System.Buffers.Text.Base64Url.DecodeFromChars(key.GetProperty("n").GetString()!),
                Exponent = System.Buffers.Text.Base64Url.DecodeFromChars(key.GetProperty("e").GetString()!),
            });

            return rsa.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException)
        {
            return false;
        }
    }
}
