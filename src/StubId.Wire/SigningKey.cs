using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StubId.Abstractions;

namespace StubId.Wire;

/// <summary>What a key is for, which decides whether and how it appears in the JWKS.</summary>
public enum KeyUse
{
    /// <summary>Signs tokens. Published as <c>use: "sig"</c>.</summary>
    Signing,

    /// <summary>Decrypts request objects. Published as <c>use: "enc"</c>.</summary>
    Encryption,
}

/// <summary>
/// A certificate-backed key, and the JWKS members derived from it.
/// </summary>
/// <remarks>
/// Keys are certificates rather than bare RSA pairs because the broker's are, and the
/// difference is visible on the wire: the <c>kid</c> is the certificate's thumbprint and
/// <c>x5c</c> carries the certificate itself. A stub built on bare keys would have to invent
/// both.
/// </remarks>
[Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
    Evidence = "fixtures/neb/pp/CAP-002")]
public sealed class SigningKey : IDisposable
{
    private readonly X509Certificate2 _certificate;

    public SigningKey(X509Certificate2 certificate, KeyUse use)
    {
        _certificate = certificate;
        Use = use;

        // .NET already returns the thumbprint as uppercase hex, which is the form the broker
        // publishes.
        Kid = certificate.Thumbprint;
        X5t = Base64Url.Encode(Convert.FromHexString(certificate.Thumbprint));
        X5c = Convert.ToBase64String(certificate.RawData);
    }

    public KeyUse Use { get; }

    /// <summary>Uppercase 40-character hex: the certificate's SHA-1 thumbprint.</summary>
    public string Kid { get; }

    public string X5t { get; }

    /// <summary>The certificate, base64 (not base64url), as a JWKS <c>x5c</c> entry.</summary>
    public string X5c { get; }

    public string UseValue => Use == KeyUse.Signing ? "sig" : "enc";

    public RSA PublicKey => _certificate.GetRSAPublicKey()
        ?? throw new InvalidOperationException($"Certificate {Kid} carries no RSA public key.");

    public RSA PrivateKey => _certificate.GetRSAPrivateKey()
        ?? throw new InvalidOperationException($"Certificate {Kid} carries no RSA private key.");

    public X509Certificate2 Certificate => _certificate;

    public void Dispose() => _certificate.Dispose();
}
