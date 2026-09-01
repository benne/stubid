using System.Security.Cryptography.X509Certificates;
using StubId.Wire;

namespace StubId.Wire.Tests;

/// <summary>
/// The certificate StubID exports is enough, by itself, to trust the instance that served it.
/// </summary>
/// <remarks>
/// A self-signed leaf is its own trust anchor or it is nothing. The certificates guide hands this
/// file to curl, to node and to keytool and tells the reader it is all they need; if it could not
/// verify itself, every one of those recipes would fail on the reader's machine and nothing here
/// would have noticed.
/// <para>
/// Hermetic on purpose. No machine store is read, none is written, and nothing is dialled - the
/// claim is about the bytes rather than about this machine's configuration, and it has to hold the
/// same way on Linux and on Windows, whose chain engines are not the same code.
/// </para>
/// </remarks>
public class TrustAnchorTests
{
    private static X509Certificate2 Create(params string[] names) =>
        CertificateFactory.CreateServerCertificate(
            "StubID", names, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    /// <remarks>
    /// The trailing newline is deliberately not asserted here. Export writes none - the last thing
    /// it emits is the ending boundary - which is why the route that serves this appends one. That
    /// guarantee belongs to the served body, and ContainerTlsTests checks it there.
    /// </remarks>
    [Fact]
    public void The_exported_text_reads_back_as_the_same_certificate()
    {
        using var certificate = Create("localhost", "127.0.0.1");

        var pem = certificate.ExportCertificatePem();

        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);

        // One block, not a chain. A caller appending a second instance's certificate to this file
        // is doing something the format allows; what we serve is one certificate.
        Assert.Equal(1, pem.Split("-----BEGIN CERTIFICATE-----").Length - 1);

        using var readBack = X509Certificate2.CreateFromPem(pem);

        Assert.Equal(certificate.Thumbprint, readBack.Thumbprint);
        Assert.False(readBack.HasPrivateKey, "The exported text carried a private key.");
    }

    [Fact]
    public void The_exported_certificate_verifies_as_its_own_trust_anchor()
    {
        using var served = Create("localhost", "127.0.0.1");
        using var anchor = X509Certificate2.CreateFromPem(served.ExportCertificatePem());

        Assert.True(
            Verifies(presented: served, against: anchor),
            "The exported certificate did not verify against itself. Every recipe in "
            + "docs/guides/certificates.md depends on it doing so.");
    }

    /// <remarks>
    /// What keeps the fact above from being vacuous, and the same discrimination
    /// ContainerTlsTests makes for the trusting handler: same subject, same names, different key.
    /// A trust anchor that accepted this would not be trusting an instance, it would be trusting
    /// the shape of a certificate.
    /// </remarks>
    [Fact]
    public void A_different_certificate_with_the_same_names_does_not_verify()
    {
        using var served = Create("localhost", "127.0.0.1");
        using var somebodyElse = Create("localhost", "127.0.0.1");

        Assert.False(Verifies(presented: served, against: somebodyElse));
    }

    private static bool Verifies(X509Certificate2 presented, X509Certificate2 against)
    {
        using var chain = new X509Chain();

        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(against);

        // Both of these are what keep the test hermetic. The default policy is willing to go to the
        // network for a revocation list, and a cross-platform build job that reaches the internet to
        // decide a certificate question fails differently on a bad day.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        return chain.Build(presented);
    }
}
