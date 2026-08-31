using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StubId.Wire;

namespace StubId.Wire.Tests;

/// <summary>
/// A certificate StubID serves TLS with is one a current client will actually accept.
/// </summary>
/// <remarks>
/// The failure this prevents is quiet and expensive to diagnose. A certificate with a correct
/// subject and no subject alternative name is refused by .NET, Chrome, Node and Java alike, and the
/// error none of them give is "the certificate has no subject alternative name" - the adopter sees
/// a handshake failure and goes looking at their own trust configuration.
/// </remarks>
public class ServerCertificateTests
{
    private static X509Certificate2 Create(params string[] names) =>
        CertificateFactory.CreateServerCertificate(
            "StubID", names, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    [Fact]
    public void The_names_a_client_checks_are_the_ones_it_was_given()
    {
        using var certificate = Create("localhost", "stubid", "127.0.0.1", "::1");

        var san = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .Single();

        Assert.Equal(["localhost", "stubid"], san.EnumerateDnsNames());
        Assert.Equal(
            [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("::1")],
            san.EnumerateIPAddresses());
    }

    /// <remarks>
    /// An address written as a DNS entry matches nothing. A client dialling 127.0.0.1 compares it
    /// against the address entries only, so the certificate would be correct-looking and useless.
    /// </remarks>
    [Fact]
    public void An_address_is_recorded_as_an_address_and_not_as_a_name()
    {
        using var certificate = Create("127.0.0.1");

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Empty(san.EnumerateDnsNames());
        Assert.Equal([IPAddress.Loopback], san.EnumerateIPAddresses());
    }

    [Fact]
    public void Serving_is_what_the_certificate_says_it_is_for()
    {
        using var certificate = Create("localhost");

        var usages = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single()
            .EnhancedKeyUsages
            .OfType<Oid>()
            .Select(o => o.Value)
            .ToList();

        Assert.Contains("1.3.6.1.5.5.7.3.1", usages);
    }

    /// <remarks>
    /// Refused at the point of creation rather than at the first handshake, which is a machine and
    /// an hour away from whoever configured it.
    /// </remarks>
    [Fact]
    public void A_certificate_with_no_names_is_refused_rather_than_issued()
    {
        Assert.Throws<ArgumentException>(() => Create());
        Assert.Throws<ArgumentException>(() => Create("", "   "));
    }

    [Fact]
    public void The_certificate_carries_a_private_key_so_it_can_serve_with_it()
    {
        using var certificate = Create("localhost");

        Assert.True(certificate.HasPrivateKey);
    }
}
