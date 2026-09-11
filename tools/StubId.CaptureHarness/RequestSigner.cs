using System.Security.Cryptography;
using System.Text;

namespace StubId.CaptureHarness;

/// <summary>
/// What signs a request object, and what the header has to say about it.
/// </summary>
/// <remarks>
/// <para>
/// The first broker accepts HS256 over the client secret, which was measured rather than assumed
/// and is in docs/research/signed-requests.md. The second advertises nine algorithms in
/// <c>request_object_signing_alg_values_supported</c> and every one of them is asymmetric — no
/// <c>HS*</c> at all — so a harness that can only sign symmetrically cannot reach its transaction
/// consent flow, which is the one MitID feature in scope beyond login.
/// </para>
/// <para>
/// Both stay. The first broker's recordings were made with HS256 and re-signing them differently
/// would change the segment lengths its fixtures record.
/// </para>
/// <para>
/// A signer holds its key for as long as it lives and does not dispose it: the process signs a
/// handful of objects and exits, and a signer whose key had been disposed under it would fail on
/// the second step of a sitting rather than the first.
/// </para>
/// </remarks>
public sealed record RequestSigner
{
    private RequestSigner(string algorithm, string? keyId, Func<byte[], byte[]> sign)
    {
        Algorithm = algorithm;
        KeyId = keyId;
        Sign = sign;
    }

    /// <summary>The JOSE algorithm name, which goes in the header.</summary>
    public string Algorithm { get; }

    /// <summary>
    /// Which registered key this is, or null where the secret identifies it.
    /// </summary>
    /// <remarks>
    /// A broker holding more than one public key for a client needs telling which one verifies
    /// an object. A client holding exactly one may resolve it without being told, but that is a
    /// guess rather than a rule, so <c>check</c> says so rather than assuming either way.
    /// </remarks>
    public string? KeyId { get; }

    /// <summary>Signs the signing input, which is the header and payload joined by a dot.</summary>
    public Func<byte[], byte[]> Sign { get; }

    /// <summary>
    /// What a configured key is, and the public half to register, so the two can be compared.
    /// </summary>
    /// <param name="Algorithm">The JOSE algorithm this key signs with.</param>
    /// <param name="Size">The key size in bits.</param>
    /// <param name="PublicKeyPem">The half to hand the broker.</param>
    /// <param name="Thumbprint">SHA-256 of the public key, to check the registered one is this one.</param>
    public sealed record KeyDescription(string Algorithm, int Size, string PublicKeyPem, string Thumbprint);

    /// <summary>
    /// Reads a private key and describes it without signing anything.
    /// </summary>
    /// <remarks>
    /// Printing the public half is the point. Registering a key is a copy between two windows,
    /// and the failure it invites - a key registered that is not the key being signed with -
    /// produces a refusal the broker's error page declines to explain. A thumbprint on both
    /// sides settles it in a glance.
    /// </remarks>
    public static KeyDescription Describe(string pem)
    {
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return new KeyDescription(
                "RS256", rsa.KeySize, rsa.ExportSubjectPublicKeyInfoPem(),
                Thumbprint(rsa.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            // Not RSA. Fall through and read it as elliptic.
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                "That file is not a private key this can sign with. It takes a PEM holding an "
                + "RSA or an EC private key.");
        }

        return new KeyDescription(
            ecdsa.KeySize switch { 256 => "ES256", 384 => "ES384", _ => "ES512" },
            ecdsa.KeySize,
            ecdsa.ExportSubjectPublicKeyInfoPem(),
            Thumbprint(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    private static string Thumbprint(byte[] publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKey));

    /// <summary>HMAC over the client secret, which is what the first broker accepts.</summary>
    public static RequestSigner ClientSecret(string secret) =>
        new("HS256", null, input => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), input));

    /// <summary>
    /// A private key the harness holds, whose public half is registered on the client.
    /// </summary>
    /// <remarks>
    /// The key has to be reproducible from a checkout and a local settings file, which is why the
    /// public half is registered by importing one rather than by having the broker generate the
    /// pair: a private half that existed on the broker's side first, and was shown once, is not
    /// something a recording can be re-made from.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The text is not a private key this can use.</exception>
    public static RequestSigner PrivateKey(string pem, string? keyId)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return new RequestSigner(
                "RS256",
                keyId,
                input => rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException(
                "That file is not a private key this can sign with. It takes a PEM holding an "
                + "RSA or an EC private key.");
        }

        var (algorithm, hash) = ecdsa.KeySize switch
        {
            256 => ("ES256", HashAlgorithmName.SHA256),
            384 => ("ES384", HashAlgorithmName.SHA384),
            _ => ("ES512", HashAlgorithmName.SHA512),
        };

        // The concatenated form, not the DER one .NET writes by default. A JWS signature is r and
        // s at fixed width; a DER-encoded signature verifies nowhere and looks like a bad key.
        return new RequestSigner(
            algorithm,
            keyId,
            input => ecdsa.SignData(
                input, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
