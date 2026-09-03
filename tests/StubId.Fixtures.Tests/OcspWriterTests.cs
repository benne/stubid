using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StubId.CaptureHarness;
using StubId.Wire;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The OCSP response StubID writes, read back by the decoder that reads the broker's.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are deliberately not written to agree with each other. The reader was built
/// against three recorded responses months before there was anything to write; putting the
/// writer's output through it is the closest thing available to checking StubID's bytes against
/// the broker's grammar, because the recordings themselves are answers about the broker's
/// certificates and cannot be reproduced by a stub that does not hold them.
/// </para>
/// <para>
/// Checked once against an implementation that is neither: <c>openssl ocsp -respin</c> parses a
/// written response, decodes every field including the archive cutoff, and answers
/// <c>Response verify OK</c> and <c>good</c> when given the responder as a trusted validator.
/// That is not a test here because it would need OpenSSL on every build agent, Windows included,
/// for an assertion the round trip below already makes portably.
/// </para>
/// <para>
/// What this cannot prove is in
/// <c>docs/brokers/neb/divergences.md#the-oces3-certificate-chain</c>: StubID has no issuing CA,
/// so its responder is self-signed, and the two relations that hold on every recording — the
/// responder having been issued by the certificate's own CA, and <c>issuerKeyHash</c> matching
/// an Authority Key Identifier — do not hold here. Both are asserted as absences below rather
/// than left to be discovered by someone reading the recordings and expecting them.
/// </para>
/// </remarks>
public class OcspWriterTests
{
    private const string ArchiveCutoff = "1.3.6.1.5.5.7.48.1.6";
    private const string OcspNoCheck = "1.3.6.1.5.5.7.48.1.5";
    private const string OcspSigning = "1.3.6.1.5.5.7.3.9";

    /// <summary>Whole seconds, so a fractional part is the writer's doing and not the clock's.</summary>
    private static readonly DateTimeOffset Moment = new(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);

    private static X509Certificate2 Subject() =>
        CertificateFactory.Create("StubID transaction-signing", Moment.AddDays(-1), Moment.AddYears(5));

    private static X509Certificate2 Responder() =>
        CertificateFactory.CreateOcspResponder(
            "StubID ocsp-responder", Moment.AddDays(-1), Moment.AddYears(5));

    private static OcspResponse RoundTrip(DateTimeOffset? producedAt = null)
    {
        using var subject = Subject();
        using var responder = Responder();

        return Ocsp.Read(OcspWriter.Good(subject, responder, producedAt ?? Moment));
    }

    [Fact]
    public void The_response_is_successful_and_carries_a_basic_response()
    {
        var response = RoundTrip();

        Assert.Equal(0, response.ResponseStatus);
        Assert.Equal("1.3.6.1.5.5.7.48.1.1", response.ResponseType);

        // version is DEFAULT v1 and no recording encodes it. DER forbids writing a value equal
        // to a DEFAULT, so emitting the [0] would be malformed as well as unrecorded.
        Assert.Equal(1, response.Version);
    }

    [Fact]
    public void The_answer_is_good_and_names_the_certificate_it_is_about()
    {
        using var subject = Subject();
        using var responder = Responder();

        var single = Assert.Single(
            Ocsp.Read(OcspWriter.Good(subject, responder, Moment)).Responses);

        Assert.Equal("good", single.CertStatus);

        // certStatus good is 80 00 - context-specific, primitive, zero-length - and not a
        // universal NULL. A writer reaching for WriteNull() without the tag produces 05 00,
        // which is a different value in the same slot and decodes as something else entirely.
        Assert.True(Ocsp.Matches(single, subject));

        // SHA-1, which is what RFC 6960 specifies for a CertID rather than anything anyone
        // chose recently.
        Assert.Equal("1.3.14.3.2.26", single.HashAlgorithm);

        // Ocsp.Matches deliberately leaves issuerKeyHash out, because for the recordings the
        // issuer's certificate is not available to compute it from. That reason does not apply
        // to a self-signed subject, where the issuer's key is the subject's own - so the field
        // is asserted here rather than inheriting an exclusion made for a different situation.
        Assert.Equal(
            Convert.ToHexString(OcspWriter.KeyHash(subject)),
            single.IssuerKeyHash);
    }

    [Fact]
    public void The_serial_is_the_certificate_s_own_bytes_pad_included()
    {
        using var subject = Subject();
        using var responder = Responder();

        var single = Assert.Single(
            Ocsp.Read(OcspWriter.Good(subject, responder, Moment)).Responses);

        // Compared against the encoded content octets rather than a parsed number: a serial
        // with the high bit set carries a leading zero pad, and normalising it away on either
        // side would make two different encodings compare equal.
        Assert.Equal(
            Convert.ToHexString(subject.SerialNumberBytes.Span),
            single.SerialNumber);
    }

    [Fact]
    public void The_responder_names_itself_by_the_key_of_the_certificate_it_enclosed()
    {
        using var responder = Responder();
        using var subject = Subject();

        var response = Ocsp.Read(OcspWriter.Good(subject, responder, Moment));

        // byKey, not byName: the recordings identify the responder by a hash of its public key.
        Assert.Null(response.ResponderName);
        Assert.Equal(Ocsp.KeyHashOf(responder), response.ResponderKeyHash);

        var enclosed = Assert.Single(response.Certificates);
        Assert.Equal(responder.RawData, enclosed);
    }

    [Fact]
    public void The_signature_verifies_against_the_certificate_the_response_carries()
    {
        var response = RoundTrip();

        using var enclosed = X509CertificateLoader.LoadCertificate(response.Certificates.Single());
        using var key = enclosed.GetECDsaPublicKey();

        Assert.Equal("1.2.840.10045.4.3.2", response.SignatureAlgorithm);

        // The format argument is not optional in practice. The overload without it expects the
        // fixed-width r||s concatenation and answers false rather than throwing, which is the
        // same thing a corrupt response looks like.
        Assert.True(key!.VerifyData(response.TbsResponseData, response.Signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        // The negative, so the assertion above is known to be testing the format rather than
        // passing for some other reason.
        Assert.False(key.VerifyData(response.TbsResponseData, response.Signature,
            HashAlgorithmName.SHA256));
    }

    [Fact]
    public void The_responder_certificate_may_sign_OCSP_and_nothing_else()
    {
        using var responder = Responder();

        var purposes = responder.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.OfType<Oid>())
            .Select(o => o.Value)
            .ToList();

        Assert.Equal([OcspSigning], purposes);

        // id-pkix-ocsp-nocheck, as the recorded responders carry: it tells a client not to go
        // looking for an OCSP response about the OCSP responder.
        Assert.Contains(responder.Extensions, e => e.Oid?.Value == OcspNoCheck);
    }

    [Fact]
    public void There_is_no_nonce_and_no_response_level_extension_at_all()
    {
        // A negative, and negatives are what a writer adds back by accident. Neither half of
        // the recorded pair carries a nonce, so a client that requires one gets nothing from
        // StubID either - which is the behaviour being copied.
        Assert.Empty(RoundTrip().ResponseExtensions);
    }

    [Fact]
    public void The_only_single_response_extension_is_a_non_critical_archive_cutoff()
    {
        using var subject = Subject();
        using var responder = Responder();

        var single = Assert.Single(
            Ocsp.Read(OcspWriter.Good(subject, responder, Moment)).Responses);
        var extension = Assert.Single(single.SingleExtensions);

        Assert.Equal(ArchiveCutoff, extension.Oid);
        Assert.False(extension.Critical);

        // A fixed instant rather than an offset from the answer. The recorded cutoff is the
        // same 2021 date on recordings taken days apart, because it says how far back the
        // responder keeps answers - not how old this one is. StubID's is the moment its own
        // certificate began, since nothing before that can be asked about.
        var reader = new AsnReader(extension.Value, AsnEncodingRules.DER);
        Assert.Equal(subject.NotBefore.ToUniversalTime(),
            reader.ReadGeneralizedTime().UtcDateTime);
        reader.ThrowIfNotEmpty();
    }

    [Fact]
    public void producedAt_equals_thisUpdate_and_nextUpdate_comes_after_both()
    {
        var response = RoundTrip();
        var single = Assert.Single(response.Responses);

        Assert.Equal(Moment, response.ProducedAt);
        Assert.Equal(response.ProducedAt, single.ThisUpdate);
        Assert.Equal(Moment + OcspWriter.Validity, single.NextUpdate);
    }

    [Fact]
    public void Every_time_is_whole_seconds()
    {
        // RFC 5280 section 4.1.2.5.2 forbids a fractional part in a PKIX GeneralizedTime, and
        // AsnWriter writes one unless told not to - so a moment taken straight off the clock
        // produces a response that decodes cleanly and does not conform. Every recorded time
        // is YYYYMMDDHHMMSSZ.
        var response = RoundTrip(Moment.AddMilliseconds(457));
        var single = Assert.Single(response.Responses);

        Assert.Equal(Moment, response.ProducedAt);
        Assert.Equal(0, single.ThisUpdate.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(0, single.NextUpdate!.Value.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void The_response_does_not_reproduce_the_two_relations_a_real_chain_would()
    {
        // Stated as a test because both are asserted of the recordings in
        // OcspResponseContractTests, and someone reading those and then this writer would
        // reasonably expect them to hold here. They do not, and the reason is that StubID has
        // no issuing CA: every certificate it makes is self-signed with CA=false, and .NET
        // refuses such a certificate as an issuer.
        using var subject = Subject();
        using var responder = Responder();

        var single = Assert.Single(
            Ocsp.Read(OcspWriter.Good(subject, responder, Moment)).Responses);

        // The recorded responder is a delegated one issued by the same CA as the certificate
        // being asked about, so its issuer name hashes to the CertID's issuerNameHash. StubID's
        // responder is its own issuer, so it does not.
        Assert.NotEqual(
            Convert.ToHexString(SHA1.HashData(responder.IssuerName.RawData)),
            single.IssuerNameHash);

        // And there is no Authority Key Identifier to match, because a self-signed certificate
        // made here carries none. The recordings' equality is that CA's practice rather than a
        // derivation, and this is the reason not to reproduce it by copying one.
        Assert.Null(Ocsp.AuthorityKeyIdentifierOf(subject));
    }
}
