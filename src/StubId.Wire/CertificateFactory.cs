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

    /// <summary>
    /// A certificate for serving TLS, as opposed to the ones StubID signs tokens with.
    /// </summary>
    /// <remarks>
    /// The difference that matters is the subject alternative name. No current client reads the
    /// common name to decide whether a certificate matches the host it dialled - .NET, Chrome, Node
    /// and Java all stopped years ago - so a certificate carrying only a subject is one every one of
    /// them refuses, and refuses with an error that says nothing about the missing extension.
    /// <para>
    /// Every name is self-signed and StubID's own. This certificate secures a transport to an
    /// emulator; it asserts nothing about identity, and no part of it should ever be trusted beyond
    /// the instance that presented it.
    /// </para>
    /// </remarks>
    public static X509Certificate2 CreateServerCertificate(
        string commonName,
        IEnumerable<string> subjectAlternativeNames,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        int keySizeBits = 2048)
    {
        ArgumentNullException.ThrowIfNull(subjectAlternativeNames);

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

        // Without server authentication in the extended key usage, a certificate that is otherwise
        // correct is still refused for serving.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        var added = 0;

        foreach (var name in subjectAlternativeNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // An address written as a name matches nothing: a client dialling 127.0.0.1 compares it
            // against the address entries and never looks at the DNS ones.
            if (System.Net.IPAddress.TryParse(name.Trim(), out var address))
            {
                names.AddIpAddress(address);
            }
            else
            {
                names.AddDnsName(name.Trim());
            }

            added++;
        }

        if (added == 0)
        {
            throw new ArgumentException(
                "A server certificate needs at least one subject alternative name; every current "
                + "client refuses one that carries none.",
                nameof(subjectAlternativeNames));
        }

        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
