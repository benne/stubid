using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StubId.Wire;

/// <summary>
/// Creates the self-signed certificates StubID signs with.
/// </summary>
/// <remarks>
/// Subject names are StubID's own. Reproducing a broker's certificate subjects would put
/// their name on a certificate they did not issue, which is a different thing entirely from
/// emulating a protocol.
/// </remarks>
public static class CertificateFactory
{
    /// <summary>
    /// Generating RSA keys at startup is the single slowest thing a container can do, so
    /// deployments that care about boot time load keys instead of calling this.
    /// </summary>
    public static X509Certificate2 Create(
        string commonName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        int keySizeBits = 2048)
    {
        using var rsa = RSA.Create(keySizeBits);

        var request = new CertificateRequest(
            $"CN={commonName}, O=StubID, C=DK",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
