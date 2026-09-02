using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the OCSP response served beside the transaction token says, and what it binds to.
/// </summary>
/// <remarks>
/// <para>
/// Three recordings carry one and nothing read any of them. The pairing test next door asserts
/// the member is present and never looked inside it, so what a Danish state responder actually
/// says about the certificate that signs a transaction token has been sitting in the tree as
/// 1808 characters of base64. This is the runbook's step 10, item 4, which asked for the decode
/// and the CertID match and got them by hand, once.
/// </para>
/// <para>
/// These assert the recordings, not the server: StubID issues no OCSP response, see
/// docs/brokers/neb/divergences.md. Facts belonging to one day's responder - which of the CA's
/// two instances answered, when, and when its certificate expires - are written into
/// fixtures/README.md and deliberately not asserted here.
/// </para>
/// </remarks>
public class OcspResponseContractTests
{
    private const string TransactKid = "7FF447FA0FB65A7E749E8B43AC635862381F0CC3";
    private const string ArchiveCutoff = "1.3.6.1.5.5.7.48.1.6";
    private const string OcspSigning = "1.3.6.1.5.5.7.3.9";
    private const string OcspNoCheck = "1.3.6.1.5.5.7.48.1.5";

    /// <summary>The three recordings that carry an OCSP response. There are no others.</summary>
    public static TheoryData<string> EveryRecording() => ["CAP-021", "CAP-022", "CAP-031"];

    private static string Served(string caseId)
    {
        using var body = JsonDocument.Parse(
            File.ReadAllText(Repository.SessionFixture(caseId, "token", "response.raw")));

        return body.RootElement.GetProperty("transaction_token_ocsp_resp").GetString()!;
    }

    private static OcspResponse Decoded(string caseId) =>
        Ocsp.Read(Convert.FromBase64String(Served(caseId)));

    /// <summary>The certificate the answers are about, from the published key set.</summary>
    private static X509Certificate2 TransactCertificate()
    {
        using var jwks = JsonDocument.Parse(
            File.ReadAllText(Repository.Fixture("CAP-002", "response.raw")));

        var key = jwks.RootElement.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("kid").GetString() == TransactKid);

        return X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(key.GetProperty("x5c")[0].GetString()!));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_response_is_standard_base64_where_the_tokens_beside_it_are_base64url(
        string caseId)
    {
        // Every other encoded value in this same response is base64url - the tokens, their
        // segments, the transaction text. This one is not, and a decoder reaching for the
        // base64url reader it used four members ago throws on the first + or /.
        var served = Served(caseId);

        Assert.True(served.Contains('+', StringComparison.Ordinal)
                    || served.Contains('/', StringComparison.Ordinal),
            "Nothing here distinguishes standard base64 from base64url.");
        Assert.DoesNotContain('-', served);
        Assert.DoesNotContain('_', served);

        // Also proves the scrubber has not been through it: this body is rewritten twice, once
        // when the sitting is written and again by sanitise, and a blob inside a JSON string is
        // exactly the shape a future redaction rule mangles without anyone noticing.
        Assert.Equal(served, Convert.ToBase64String(Convert.FromBase64String(served)));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void Every_recording_decodes_as_a_successful_basic_ocsp_response(string caseId)
    {
        var response = Decoded(caseId);

        Assert.Equal(0, response.ResponseStatus);
        Assert.Equal("1.3.6.1.5.5.7.48.1.1", response.ResponseType);

        // Version 1 is a DEFAULT and is absent from the encoding - there is no [0] on the wire
        // in any of the three. A decoder that requires one reads none of these.
        Assert.Equal(1, response.Version);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void One_answer_names_the_transact_certificate_and_says_good(string caseId)
    {
        // Step 10's fourth item. The responder answers about exactly one certificate, and it is
        // the one the JWKS publishes as CN=NEB Transact PP - established here rather than
        // assumed from the two arriving in the same response.
        var response = Decoded(caseId);
        using var certificate = TransactCertificate();

        var single = Assert.Single(response.Responses);

        Assert.True(Ocsp.Matches(single, certificate),
            $"The CertID does not name the Transact certificate: serial {single.SerialNumber} "
            + $"against {Convert.ToHexString(certificate.SerialNumberBytes.Span)}.");
        Assert.Equal("good", single.CertStatus);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_issuer_name_hash_is_over_the_issuers_name_and_not_its_certificate(
        string caseId)
    {
        // Written out rather than left inside Matches, because this is the derivation somebody
        // implementing the other side has to get right, and it is a hash of the issuer's name
        // as encoded in the subject certificate - not of the issuer's certificate, and not of
        // the subject's own name.
        var single = Assert.Single(Decoded(caseId).Responses);
        using var certificate = TransactCertificate();

        Assert.Equal("1.3.14.3.2.26", single.HashAlgorithm);
        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(certificate.IssuerName.RawData)),
            single.IssuerNameHash);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_issuer_key_hash_matches_the_authority_key_identifier_by_this_CAs_practice(
        string caseId)
    {
        // Equal, and not derived. RFC 6960 makes issuerKeyHash a hash of the issuer's public
        // key; RFC 5280 offers that same hash as one way of filling in an authority key
        // identifier, and this CA takes it. The issuer's certificate is in neither the response
        // nor this repository, so the RFC 6960 value cannot be computed here at all - which
        // means a future StubID must not produce one by copying an AKI because this passes.
        var single = Assert.Single(Decoded(caseId).Responses);
        using var certificate = TransactCertificate();

        Assert.True(Ocsp.IssuerKeyHashEqualsAuthorityKeyIdentifier(single, certificate));

        // The second witness: the responder's own certificate, issued by the same CA, names the
        // same authority key. Two certificates agreeing is what makes this practice rather than
        // coincidence.
        using var responder = X509CertificateLoader.LoadCertificate(
            Decoded(caseId).Certificates.Single());

        Assert.Equal(single.IssuerKeyHash, Ocsp.AuthorityKeyIdentifierOf(responder));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_issuer_key_hash_is_not_the_certificates_own_key_hash(string caseId)
    {
        // The confusion available on first contact: two twenty-byte SHA-1s that both belong to
        // the same certificate. One hashes the issuer's key, the other its own. Reading the
        // wrong one gives a CertID that names nothing and looks like a rotation.
        var single = Assert.Single(Decoded(caseId).Responses);
        using var certificate = TransactCertificate();

        Assert.NotEqual(Ocsp.KeyHashOf(certificate), single.IssuerKeyHash);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_responder_names_itself_by_the_key_of_the_certificate_it_carries(string caseId)
    {
        // Two responder instances answered across the two sittings, so the identifier itself is
        // not a fact about the broker. The relation is: whichever instance answers, it names
        // itself by the hash of the key in the certificate it encloses - and that hash is over
        // the public key's bits, not over the whole SubjectPublicKeyInfo, which is a different
        // value and the easiest way to implement this wrongly.
        var response = Decoded(caseId);
        using var responder = X509CertificateLoader.LoadCertificate(
            response.Certificates.Single());

        Assert.Null(response.ResponderName);
        Assert.Equal(Ocsp.KeyHashOf(responder), response.ResponderKeyHash);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_response_is_signed_by_a_delegated_responder_the_same_CA_issued(string caseId)
    {
        // Delegation, decided entirely inside the blob: the certificate that signed the answer
        // is not the CA, it is a responder the CA issued for the purpose. Both halves matter -
        // the signing purpose is the only one it has, and it carries the extension that tells a
        // client not to ask whether the responder itself is revoked.
        var response = Decoded(caseId);
        using var responder = X509CertificateLoader.LoadCertificate(
            response.Certificates.Single());

        var purposes = responder.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.OfType<Oid>())
            .Select(o => o.Value)
            .ToList();

        Assert.Equal([OcspSigning], purposes);
        Assert.Contains(responder.Extensions, e => e.Oid?.Value == OcspNoCheck);

        var single = Assert.Single(response.Responses);
        Assert.Equal(
            single.IssuerNameHash,
            Convert.ToHexString(SHA1.HashData(responder.IssuerName.RawData)));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_signature_verifies_against_the_certificate_the_response_carries(string caseId)
    {
        // What this proves is that the recorded bytes are intact and that the enclosed
        // certificate is the one that signed them. It proves nothing about whether that
        // responder was allowed to answer for this CA, and nothing about whether the answer was
        // valid at any moment: both need the issuing certificate, which is not committed, and
        // the second needs a clock. No chain is built, no store is read, and nothing is
        // downloaded - two of these three responder certificates have already expired.
        var response = Decoded(caseId);
        using var responder = X509CertificateLoader.LoadCertificate(
            response.Certificates.Single());
        using var key = responder.GetECDsaPublicKey();

        // The format argument is not optional in practice. The overload without it expects the
        // fixed-width r||s concatenation and answers false rather than throwing, which reads
        // exactly like a corrupt recording.
        Assert.True(key!.VerifyData(response.TbsResponseData, response.Signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_response_carries_no_nonce_and_no_response_level_extensions_at_all(
        string caseId)
    {
        // A negative, and negatives are what a stub adds back by accident. It is also the OCSP
        // side of the token's missing signing_cert_ocsp_nonce: neither half of this pair
        // carries a nonce, so a client that requires one gets nothing from this broker.
        Assert.Empty(Decoded(caseId).ResponseExtensions);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_only_single_response_extension_is_a_non_critical_archive_cutoff(string caseId)
    {
        // Shape and criticality, not the instant. The cutoff is the same in all three and is
        // five years before any of them, which makes it a property of the CA's archive policy
        // rather than of anything the broker did.
        var single = Assert.Single(Decoded(caseId).Responses);
        var extension = Assert.Single(single.SingleExtensions);

        Assert.Equal(ArchiveCutoff, extension.Oid);
        Assert.False(extension.Critical);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void producedAt_equals_thisUpdate_and_nextUpdate_comes_after_both(string caseId)
    {
        // Relations only. The instants differ per recording and belong in the fixture README;
        // asserting them here would pin the minute a responder happened to answer. Compared as
        // instants rather than as text, because this build does not run invariant.
        var response = Decoded(caseId);
        var single = Assert.Single(response.Responses);

        Assert.Equal(response.ProducedAt, single.ThisUpdate);
        Assert.NotNull(single.NextUpdate);
        Assert.True(single.NextUpdate > single.ThisUpdate,
            $"nextUpdate {single.NextUpdate:O} does not follow thisUpdate {single.ThisUpdate:O}.");
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void The_response_is_signed_with_a_different_algorithm_from_the_token_beside_it(
        string caseId)
    {
        // One response body, two signatures, two algorithms and two key types: the token is
        // RSA and the OCSP answer beside it is elliptic curve. A stub with one signing key
        // cannot produce this pair, which is worth knowing before someone tries.
        using var header = JsonDocument.Parse(File.ReadAllText(
            Repository.SessionFixture(caseId, "token", "transaction_token.header.json")));

        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("1.2.840.10045.4.3.2", Decoded(caseId).SignatureAlgorithm);
    }

    [Fact]
    public void The_responder_answered_before_the_recording_that_carries_it()
    {
        // The broker does not fetch a fresh answer per request; it serves one it already had.
        // A stub minting producedAt at the moment of the token diverges from the only evidence
        // there is. Only CAP-031 can say so: the first sitting predates capturedAtUtc, so its
        // two recordings have no capture instant to compare against.
        using var meta = JsonDocument.Parse(
            File.ReadAllText(Repository.SessionFixture("CAP-031", "token", "meta.json")));

        var captured = meta.RootElement.GetProperty("capturedAtUtc").GetDateTimeOffset();
        var producedAt = Decoded("CAP-031").ProducedAt;

        Assert.True(producedAt < captured,
            $"producedAt {producedAt:O} was not before the capture at {captured:O}.");
    }

    [Fact]
    public void Every_recorded_response_that_carries_the_member_decodes()
    {
        // Walks the pack rather than the list above, so a fourth recording is covered the day
        // it lands instead of the day somebody remembers this file. The count is asserted
        // because a walk that finds nothing otherwise passes.
        var seen = 0;

        foreach (var file in Directory.EnumerateFiles(
            Repository.NebSession, "response.raw", SearchOption.AllDirectories))
        {
            // Not every recorded body is JSON - a callback is name=value lines, which is why
            // the harness's own extractor decides the same way rather than parsing first.
            var text = File.ReadAllText(file).TrimStart();
            if (!text.StartsWith('{'))
            {
                continue;
            }

            using var body = JsonDocument.Parse(text);
            if (!body.RootElement.TryGetProperty("transaction_token_ocsp_resp", out var served))
            {
                continue;
            }

            var response = Ocsp.Describe(served.GetString()!);

            Assert.NotNull(response);
            Assert.Equal(0, response.ResponseStatus);
            seen++;
        }

        Assert.Equal(3, seen);
    }
}

/// <summary>
/// The shapes the recordings do not contain, built by hand.
/// </summary>
/// <remarks>
/// Three recordings of one responder on two days exercise one path through a grammar that has
/// several. The optional version, a responder that names itself rather than hashing its key, a
/// revoked answer, an algorithm identifier without parameters and a nonce are all legal and all
/// absent, so without these the decoder has branches nothing has ever run - including the branch
/// whose emptiness the recordings are asserted on.
/// </remarks>
public class OcspReaderTests
{
    private enum Status
    {
        Successful = 0,
    }

    private static readonly DateTimeOffset Moment = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>
    /// A response with one answer in it, varied only where a test needs it to vary.
    /// </summary>
    private static byte[] Build(
        bool writeVersion = false,
        string? responderName = null,
        bool revoked = false,
        bool hashParameters = true,
        byte[]? nonce = null,
        byte[]? serial = null)
    {
        var context0 = new Asn1Tag(TagClass.ContextSpecific, 0);
        var context1 = new Asn1Tag(TagClass.ContextSpecific, 1);
        var context2 = new Asn1Tag(TagClass.ContextSpecific, 2);

        var basic = new AsnWriter(AsnEncodingRules.DER);
        basic.PushSequence();

        basic.PushSequence();
        if (writeVersion)
        {
            basic.PushSequence(context0);
            basic.WriteInteger(0);
            basic.PopSequence(context0);
        }

        if (responderName is null)
        {
            basic.PushSequence(context2);
            basic.WriteOctetString(new byte[20]);
            basic.PopSequence(context2);
        }
        else
        {
            basic.PushSequence(context1);
            basic.WriteEncodedValue(new X500DistinguishedName(responderName).RawData);
            basic.PopSequence(context1);
        }

        basic.WriteGeneralizedTime(Moment);

        basic.PushSequence();
        basic.PushSequence();
        basic.PushSequence();
        basic.PushSequence();
        basic.WriteObjectIdentifier("1.3.14.3.2.26");
        if (hashParameters)
        {
            basic.WriteNull();
        }

        basic.PopSequence();
        basic.WriteOctetString(new byte[20]);
        basic.WriteOctetString(new byte[20]);
        basic.WriteIntegerUnsigned(serial ?? [0x01]);
        basic.PopSequence();

        if (revoked)
        {
            // [1] IMPLICIT RevokedInfo, whose revocationTime this decoder deliberately skips.
            basic.PushSequence(context1);
            basic.WriteGeneralizedTime(Moment);
            basic.PopSequence(context1);
        }
        else
        {
            basic.WriteNull(context0);
        }

        basic.WriteGeneralizedTime(Moment);
        basic.PopSequence();
        basic.PopSequence();

        if (nonce is not null)
        {
            basic.PushSequence(context1);
            basic.PushSequence();
            basic.PushSequence();
            basic.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.2");
            basic.WriteOctetString(nonce);
            basic.PopSequence();
            basic.PopSequence();
            basic.PopSequence(context1);
        }

        basic.PopSequence();

        basic.PushSequence();
        basic.WriteObjectIdentifier("1.2.840.10045.4.3.2");
        basic.PopSequence();
        basic.WriteBitString([0xAB, 0xCD]);
        basic.PopSequence();

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteEnumeratedValue(Status.Successful);
        writer.PushSequence(context0);
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
        writer.WriteOctetString(basic.Encode());
        writer.PopSequence();
        writer.PopSequence(context0);
        writer.PopSequence();

        return writer.Encode();
    }

    [Fact]
    public void A_version_that_is_encoded_explicitly_reads_the_same_as_one_that_is_omitted()
    {
        // No recording carries the [0], so the branch that reads one has never run against real
        // bytes. The tag on the wire is constructed and a tag built in code is primitive, which
        // is why comparing them for equality skips this silently rather than failing.
        Assert.Equal(1, Ocsp.Read(Build(writeVersion: true)).Version);
        Assert.Equal(1, Ocsp.Read(Build()).Version);
    }

    [Fact]
    public void A_responder_that_names_itself_by_name_is_read_as_a_name()
    {
        // Both recordings' responders identify by key hash. The other arm of the choice is a
        // distinguished name, and reading it as an octet string throws.
        var response = Ocsp.Read(Build(responderName: "CN=A Responder, C=DK"));

        Assert.Null(response.ResponderKeyHash);
        Assert.Equal("CN=A Responder, C=DK", response.ResponderName);
    }

    [Fact]
    public void A_revoked_status_is_classified_without_its_detail_being_invented()
    {
        // Nothing in the pack is revoked. The decoder classifies the answer and steps over
        // RevokedInfo rather than decoding a shape no fixture has ever shown it, and this is
        // what makes that a decision instead of a gap.
        var single = Assert.Single(Ocsp.Read(Build(revoked: true)).Responses);

        Assert.Equal("revoked", single.CertStatus);
    }

    [Fact]
    public void A_hash_algorithm_with_no_parameters_reads_as_one_with_an_explicit_null()
    {
        // The recordings send SHA-1 with an explicit NULL. A responder that moved to SHA-2
        // would conventionally omit it, and an absent OPTIONAL is not an error.
        Assert.Equal(
            Ocsp.Read(Build(hashParameters: true)).Responses[0].HashAlgorithm,
            Ocsp.Read(Build(hashParameters: false)).Responses[0].HashAlgorithm);
    }

    [Fact]
    public void A_nonce_extension_appears_among_the_response_extensions()
    {
        // The recordings carry no response-level extension at all, which is asserted next door
        // as a fact about this broker. That assertion is only worth something if the code that
        // would have found one has been shown to find one.
        var extension = Assert.Single(Ocsp.Read(Build(nonce: [9, 9, 9])).ResponseExtensions);

        Assert.Equal("1.3.6.1.5.5.7.48.1.2", extension.Oid);
        Assert.False(extension.Critical);
    }

    [Fact]
    public void A_serial_number_keeps_the_pad_the_responder_encoded()
    {
        // A serial whose leading byte has the high bit set is encoded with a leading zero, and
        // the padded form is what a CertID compares. Trimming it to make the value look tidier
        // would turn a match into a miss on the first certificate whose serial starts high.
        var single = Assert.Single(Ocsp.Read(Build(serial: [0xF0, 0x01])).Responses);

        Assert.Equal("00F001", single.SerialNumber);
    }
}
