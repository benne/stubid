using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StubId.Abstractions;

namespace StubId.Wire;

/// <summary>
/// Writes the OCSP response that travels beside a transaction token.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled against RFC 6960, for the same reason the reader in the capture harness is:
/// the framework has no OCSP responder, and the alternative is a first cryptography dependency
/// for a grammar that has not moved since 2013. <c>System.Formats.Asn1</c> is in the shared
/// framework, so this costs no package reference.
/// </para>
/// <para>
/// The shape is copied from the three recordings that carry one — successful, one answer,
/// <c>good</c>, a SHA-1 CertID, ECDSA on P-256, one non-critical archive-cutoff extension, no
/// nonce and no response-level extensions at all, and the responder's own certificate enclosed.
/// What is deliberately <em>not</em> copied is in
/// <c>docs/brokers/neb/divergences.md#the-oces3-certificate-chain</c>: StubID has no issuing
/// CA, so its responder is self-signed and the two relations that hold on the recordings — the
/// responder having been issued by the certificate's own CA, and <c>issuerKeyHash</c> matching
/// an Authority Key Identifier — do not hold here.
/// </para>
/// </remarks>
public static class OcspWriter
{
    private const string BasicResponse = "1.3.6.1.5.5.7.48.1.1";
    private const string ArchiveCutoff = "1.3.6.1.5.5.7.48.1.6";
    private const string Sha1Oid = "1.3.14.3.2.26";
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";

    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0);
    private static readonly Asn1Tag Context1 = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag Context2 = new(TagClass.ContextSpecific, 2);

    /// <summary>
    /// How long an answer claims to be good for. The recorded responder sets nextUpdate one
    /// second before its own certificate expires, which is this CA's practice and would make a
    /// StubID answer valid for five years; a day is the conventional interval and keeps a
    /// client that caches the answer coming back.
    /// </summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(1);

    /// <summary>
    /// A successful response saying <c>good</c> about one certificate, signed by the responder.
    /// </summary>
    /// <param name="certificate">
    /// The certificate being asked about — StubID's transaction-signing certificate. It is
    /// self-signed, so its issuer is itself and the CertID's issuer hashes are taken over its
    /// own name and key.
    /// </param>
    /// <param name="responder">
    /// The P-256 certificate that signs the answer, enclosed in the response so a client can
    /// check the signature without fetching anything.
    /// </param>
    [Fidelity(FidelityTier.Shape, FidelityProvenance.Divergent,
        Evidence = "fixtures/neb/pp-session/CAP-021/token/response.raw, "
                   + "fixtures/neb/pp-session/CAP-022/token/response.raw, "
                   + "fixtures/neb/pp-session/CAP-031/token/response.raw",
        Reason = "docs/brokers/neb/divergences.md#the-oces3-certificate-chain")]
    public static byte[] Good(
        X509Certificate2 certificate, X509Certificate2 responder, DateTimeOffset producedAt)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(responder);

        var basic = new AsnWriter(AsnEncodingRules.DER);
        basic.PushSequence();

        // tbsResponseData is written into its own writer first, because it has to be signed
        // before it can be placed, and re-encoding it afterwards would be a second chance to
        // produce different bytes from the ones the signature covers.
        var tbs = ResponseData(certificate, responder, producedAt);
        basic.WriteEncodedValue(tbs);

        basic.PushSequence();
        basic.WriteObjectIdentifier(EcdsaWithSha256);

        // No parameters. An ECDSA algorithm identifier omits them, where SHA-1's carries an
        // explicit NULL - the reader takes whatever is there, and the recordings send nothing.
        basic.PopSequence();

        using var key = responder.GetECDsaPrivateKey()
            ?? throw new ArgumentException(
                "The responder certificate has no ECDSA private key.", nameof(responder));

        // Rfc3279DerSequence, not the default. The overload without a format writes the
        // fixed-width r||s concatenation, which every OCSP client rejects - and rejects by
        // answering false rather than throwing, so the mistake reads as a corrupt response.
        basic.WriteBitString(key.SignData(tbs, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        // certs [0] EXPLICIT SEQUENCE OF Certificate. Two pushes, not one: the [0] wrapper and
        // the SEQUENCE inside it are separate structures.
        basic.PushSequence(Context0);
        basic.PushSequence();
        basic.WriteEncodedValue(responder.RawData);
        basic.PopSequence();
        basic.PopSequence(Context0);

        basic.PopSequence();

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();

        // successful(0).
        writer.WriteEnumeratedValue(OcspResponseStatus.Successful);

        writer.PushSequence(Context0);
        writer.PushSequence();
        writer.WriteObjectIdentifier(BasicResponse);

        // An OCTET STRING carrying a whole DER document of its own.
        writer.WriteOctetString(basic.Encode());
        writer.PopSequence();
        writer.PopSequence(Context0);
        writer.PopSequence();

        return writer.Encode();
    }

    /// <summary>RFC 6960's KeyHash: SHA-1 over the public key's BIT STRING contents.</summary>
    /// <remarks>
    /// Not <c>ExportSubjectPublicKeyInfo()</c>, which is the whole SubjectPublicKeyInfo
    /// structure — algorithm identifier and all — and hashes to a different value entirely.
    /// </remarks>
    public static byte[] KeyHash(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return SHA1.HashData(certificate.PublicKey.EncodedKeyValue.RawData);
    }

    private static byte[] ResponseData(
        X509Certificate2 certificate, X509Certificate2 responder, DateTimeOffset producedAt)
    {
        // Whole seconds. RFC 5280 section 4.1.2.5.2 forbids a fractional part in a PKIX
        // GeneralizedTime, and AsnWriter writes one by default - so a moment straight off the
        // clock produces a response that decodes but does not conform, and every recorded time
        // is YYYYMMDDHHMMSSZ.
        var moment = Truncate(producedAt);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();

        // version is DEFAULT v1 and every recording omits it. DER forbids encoding a value
        // equal to the default, so writing the [0] here would be wrong as well as unrecorded.

        // ResponderID byKey: [2] EXPLICIT KeyHash, itself an OCTET STRING.
        writer.PushSequence(Context2);
        writer.WriteOctetString(KeyHash(responder));
        writer.PopSequence(Context2);

        writer.WriteGeneralizedTime(moment, omitFractionalSeconds: true);

        // responses
        writer.PushSequence();
        writer.PushSequence();

        // CertID. The certificate is self-signed, so the issuer whose name and key these hash
        // is the certificate itself.
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(Sha1Oid);

        // SHA-1's algorithm identifier carries an explicit NULL where ECDSA's carries nothing.
        writer.WriteNull();
        writer.PopSequence();
        writer.WriteOctetString(SHA1.HashData(certificate.IssuerName.RawData));
        writer.WriteOctetString(KeyHash(certificate));

        // The serial exactly as the certificate encodes it, pad and all.
        writer.WriteIntegerUnsigned(certificate.SerialNumberBytes.Span);
        writer.PopSequence();

        // certStatus good: [0] IMPLICIT NULL - context-specific, primitive, zero length. Not a
        // universal NULL, which is why a reader calling ReadNull on it fails.
        writer.WriteNull(Context0);

        writer.WriteGeneralizedTime(moment, omitFractionalSeconds: true);

        // nextUpdate [0] EXPLICIT GeneralizedTime.
        writer.PushSequence(Context0);
        writer.WriteGeneralizedTime(moment + Validity, omitFractionalSeconds: true);
        writer.PopSequence(Context0);

        // singleExtensions [1] EXPLICIT Extensions: one archive cutoff, non-critical.
        //
        // A fixed instant, not an offset. The recorded cutoff is the same 2021 date in all
        // three recordings whatever day they were taken, which is what an archive cutoff is:
        // how far back the responder keeps answers, published by the CA. StubID's equivalent
        // is the moment its own certificate began, because nothing before that can be asked
        // about.
        writer.PushSequence(Context1);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(ArchiveCutoff);

        // critical is DEFAULT FALSE, so a non-critical extension writes no BOOLEAN at all.
        writer.WriteOctetString(GeneralizedTime(Truncate(certificate.NotBefore)));
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence(Context1);

        writer.PopSequence();
        writer.PopSequence();

        // responseExtensions [1] is omitted. No recording carries one, and neither half of the
        // recorded pair carries a nonce.
        writer.PopSequence();

        return writer.Encode();
    }

    /// <summary>An extnValue's contents are DER of their own, so they get their own writer.</summary>
    private static byte[] GeneralizedTime(DateTimeOffset moment)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteGeneralizedTime(moment, omitFractionalSeconds: true);

        return writer.Encode();
    }

    private static DateTimeOffset Truncate(DateTimeOffset moment) =>
        new(moment.UtcDateTime.AddTicks(-(moment.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)),
            TimeSpan.Zero);

    private enum OcspResponseStatus
    {
        Successful = 0,
    }
}
