using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StubId.CaptureHarness;

/// <summary>One extension, kept whole so an unrecognised one is still recorded as itself.</summary>
/// <param name="Oid">Dotted form.</param>
/// <param name="Value">The <c>extnValue</c> octets, which carry DER of their own.</param>
public sealed record OcspExtension(string Oid, bool Critical, byte[] Value);

/// <summary>What the responder said about one certificate.</summary>
/// <param name="HashAlgorithm">
/// Dotted OID. SHA-1 here, which is what RFC 6960 specifies for a CertID and not a choice
/// anyone made recently.
/// </param>
/// <param name="SerialNumber">
/// Hex of the INTEGER content octets exactly as encoded, a leading zero pad included if the
/// responder sent one. Not normalised: how it was encoded is a fact about the wire.
/// </param>
public sealed record OcspSingleResponse(
    string HashAlgorithm,
    string IssuerNameHash,
    string IssuerKeyHash,
    string SerialNumber,
    string CertStatus,
    DateTimeOffset ThisUpdate,
    DateTimeOffset? NextUpdate,
    IReadOnlyList<OcspExtension> SingleExtensions);

/// <summary>A decoded OCSP response.</summary>
/// <param name="TbsResponseData">
/// The signed bytes, kept exactly, so the signature stays checkable after decoding.
/// </param>
public sealed record OcspResponse(
    int ResponseStatus,
    string? ResponseType,
    int Version,
    string? ResponderName,
    string? ResponderKeyHash,
    DateTimeOffset ProducedAt,
    IReadOnlyList<OcspSingleResponse> Responses,
    IReadOnlyList<OcspExtension> ResponseExtensions,
    string SignatureAlgorithm,
    byte[] Signature,
    byte[] TbsResponseData,
    IReadOnlyList<byte[]> Certificates);

/// <summary>
/// Reads the OCSP response the broker serves beside its transaction token.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled against RFC 6960 because there is no OCSP response parser in the framework and
/// the alternative is a first cryptography dependency for a grammar that has not moved since
/// 2013. It reads what the recordings contain and stops there: enough to say which certificate
/// was asked about, what the answer was, and when.
/// </para>
/// <para>
/// StubID emits no OCSP response. This exists so the three recordings that carry one can be
/// read rather than admired, and so that whoever implements transaction signing has the shape
/// in front of them instead of a base64 string.
/// </para>
/// </remarks>
public static class Ocsp
{
    private const string Sha1 = "1.3.14.3.2.26";
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string AuthorityKeyIdentifier = "2.5.29.35";

    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0);
    private static readonly Asn1Tag Context1 = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag Context2 = new(TagClass.ContextSpecific, 2);

    /// <summary>Reads DER, and throws on anything that is not an OCSP response.</summary>
    public static OcspResponse Read(ReadOnlyMemory<byte> der)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER);
        var response = outer.ReadSequence();
        outer.ThrowIfNotEmpty();

        // Read as bytes rather than into an enum: a status this does not know about is worth
        // recording, and reading it into an enum would throw on the one response worth seeing.
        var status = response.ReadEnumeratedBytes();
        if (status.Length != 1)
        {
            throw new AsnContentException("responseStatus was not a single octet.");
        }

        if (!response.HasData)
        {
            // Legal, and what an error answer looks like: a status and nothing else.
            return new OcspResponse(status.Span[0], null, 1, null, null, default, [], [], "",
                [], [], []);
        }

        // Two reads, not one. ReadSequence(tag) substitutes the tag for the SEQUENCE's own,
        // which is the reading an IMPLICIT tag wants; these are EXPLICIT, so the [0] wrapper
        // and the SEQUENCE inside it are separate. The same shortcut on nextUpdate below would
        // not fail loudly, so the idiom is used everywhere rather than where it is noticed.
        var wrapper = response.ReadSequence(Context0);
        var bytes = wrapper.ReadSequence();
        wrapper.ThrowIfNotEmpty();
        response.ThrowIfNotEmpty();

        var responseType = bytes.ReadObjectIdentifier();
        var basicDer = bytes.ReadOctetString();
        bytes.ThrowIfNotEmpty();

        // An OCTET STRING carrying DER is not descended into. It is a new document.
        var basicOuter = new AsnReader(basicDer, AsnEncodingRules.DER);
        var basic = basicOuter.ReadSequence();
        basicOuter.ThrowIfNotEmpty();

        // Peeked before it is consumed, because this is what the signature is over.
        var tbs = basic.PeekEncodedValue().ToArray();
        var data = basic.ReadSequence();

        var version = 1;
        if (data.HasData && data.PeekTag().HasSameClassAndValue(Context0))
        {
            // Compared with HasSameClassAndValue: the tag on the wire is constructed and the
            // one built here is primitive, so == is false against the A0 that is really there.
            var explicitVersion = data.ReadSequence(Context0);
            version = (int)explicitVersion.ReadInteger() + 1;
            explicitVersion.ThrowIfNotEmpty();
        }

        var (responderName, responderKeyHash) = ReadResponderId(data);
        var producedAt = data.ReadGeneralizedTime();

        var singles = data.ReadSequence();
        var responses = new List<OcspSingleResponse>();
        while (singles.HasData)
        {
            responses.Add(ReadSingleResponse(singles.ReadSequence()));
        }

        var extensions = data.HasData && data.PeekTag().HasSameClassAndValue(Context1)
            ? ReadExtensions(data, Context1)
            : [];
        data.ThrowIfNotEmpty();

        var signatureAlgorithm = ReadAlgorithm(basic.ReadSequence());
        var signature = basic.ReadBitString(out var unusedBits);
        if (unusedBits != 0)
        {
            throw new AsnContentException("The signature BIT STRING had unused bits.");
        }

        var certificates = new List<byte[]>();
        if (basic.HasData && basic.PeekTag().HasSameClassAndValue(Context0))
        {
            var certsWrapper = basic.ReadSequence(Context0);
            var certs = certsWrapper.ReadSequence();
            while (certs.HasData)
            {
                certificates.Add(certs.ReadEncodedValue().ToArray());
            }

            certsWrapper.ThrowIfNotEmpty();
        }

        basic.ThrowIfNotEmpty();

        return new OcspResponse(status.Span[0], responseType, version, responderName,
            responderKeyHash, producedAt, responses, extensions, signatureAlgorithm, signature,
            tbs, certificates);
    }

    /// <summary>
    /// Reads a base64 response, and returns null rather than throwing on one it cannot read.
    /// </summary>
    /// <remarks>
    /// What the sitting wants. The decode is a courtesy printed in the chair, and a blob that
    /// will not parse is a finding to write down afterwards rather than a reason to lose an
    /// authentication that cannot be taken again.
    /// </remarks>
    public static OcspResponse? Describe(string base64Der)
    {
        try
        {
            return Read(Convert.FromBase64String(base64Der));
        }
        catch (Exception e) when (e is AsnContentException or FormatException
                                       or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this answer is about that certificate, by RFC 6960 section 4.1.1: the hash of
    /// the issuer's name, and the serial number.
    /// </summary>
    /// <remarks>
    /// The issuerKeyHash is deliberately not part of this. It is a hash of the issuer's public
    /// key, and the issuer's certificate is in neither the response nor this repository, so
    /// there is nothing here to compute it from - see
    /// <see cref="IssuerKeyHashEqualsAuthorityKeyIdentifier"/> for what can be said instead.
    /// </remarks>
    public static bool Matches(OcspSingleResponse single, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentNullException.ThrowIfNull(certificate);

        // Dispatched on the OID rather than assumed to be SHA-1. A responder that moves to
        // SHA-256 would otherwise stop matching, and it would read as a rotated certificate.
        var name = single.HashAlgorithm switch
        {
            Sha1 => Convert.ToHexString(SHA1.HashData(certificate.IssuerName.RawData)),
            Sha256 => Convert.ToHexString(SHA256.HashData(certificate.IssuerName.RawData)),
            _ => null,
        };

        return name is not null
               && name.Equals(single.IssuerNameHash, StringComparison.OrdinalIgnoreCase)
               && Convert.ToHexString(certificate.SerialNumberBytes.Span)
                   .Equals(single.SerialNumber, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the CertID's issuerKeyHash equals that certificate's Authority Key Identifier.
    /// </summary>
    /// <remarks>
    /// They are equal for these recordings, and the equality is this CA's practice rather than
    /// a derivation. RFC 6960 defines issuerKeyHash as a hash of the issuer's public key; RFC
    /// 5280 section 4.2.1.1 offers that same hash as one way of filling in an authority key
    /// identifier, and this CA takes it. Nothing obliges a CA to, so a future implementation
    /// must not produce an issuerKeyHash by copying an authority key identifier on the strength
    /// of this holding here.
    /// </remarks>
    public static bool IssuerKeyHashEqualsAuthorityKeyIdentifier(
        OcspSingleResponse single, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentNullException.ThrowIfNull(certificate);

        var identifier = AuthorityKeyIdentifierOf(certificate);

        return identifier is not null
               && identifier.Equals(single.IssuerKeyHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The authority key identifier as hex, or null when there is none.</summary>
    public static string? AuthorityKeyIdentifierOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var extension = certificate.Extensions[AuthorityKeyIdentifier];
        if (extension is null)
        {
            return null;
        }

        var identifier = new X509AuthorityKeyIdentifierExtension(
            extension.RawData, extension.Critical).KeyIdentifier;

        return identifier is null ? null : Convert.ToHexString(identifier.Value.Span);
    }

    /// <summary>
    /// RFC 6960's KeyHash: SHA-1 over the public key's BIT STRING contents.
    /// </summary>
    /// <remarks>
    /// Not <c>ExportSubjectPublicKeyInfo()</c>, which is the whole SubjectPublicKeyInfo
    /// structure - algorithm identifier and all - and hashes to a different value entirely.
    /// </remarks>
    public static string KeyHashOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return Convert.ToHexString(SHA1.HashData(certificate.PublicKey.EncodedKeyValue.RawData));
    }

    private static (string? Name, string? KeyHash) ReadResponderId(AsnReader data)
    {
        if (data.PeekTag().HasSameClassAndValue(Context1))
        {
            var byName = data.ReadSequence(Context1);
            var name = new X500DistinguishedName(byName.ReadEncodedValue().ToArray()).Name;
            byName.ThrowIfNotEmpty();

            return (name, null);
        }

        var byKey = data.ReadSequence(Context2);
        var keyHash = Convert.ToHexString(byKey.ReadOctetString());
        byKey.ThrowIfNotEmpty();

        return (null, keyHash);
    }

    private static OcspSingleResponse ReadSingleResponse(AsnReader single)
    {
        var certId = single.ReadSequence();
        var hashAlgorithm = ReadAlgorithm(certId.ReadSequence());
        var issuerNameHash = Convert.ToHexString(certId.ReadOctetString());
        var issuerKeyHash = Convert.ToHexString(certId.ReadOctetString());

        // The content octets verbatim. ReadInteger gives a BigInteger, which would lose a
        // leading pad, and twenty bytes of serial do not fit anything smaller anyway.
        var serialNumber = Convert.ToHexString(certId.ReadIntegerBytes().Span);
        certId.ThrowIfNotEmpty();

        // good is 80 00: context-specific, primitive, empty. Not a universal NULL, so
        // ReadNull fails on it. Classify by tag and consume whatever the choice carries.
        var statusTag = single.PeekTag();
        if (statusTag.TagClass != TagClass.ContextSpecific)
        {
            throw new AsnContentException("certStatus was not a context-specific choice.");
        }

        var certStatus = statusTag.TagValue switch
        {
            0 => "good",
            1 => "revoked",
            2 => "unknown",
            _ => "unrecognised",
        };

        // RevokedInfo is consumed and not decoded. Nothing in the recordings is revoked, and a
        // parser for a shape no fixture exercises is a claim rather than a capability.
        single.ReadEncodedValue();

        var thisUpdate = single.ReadGeneralizedTime();

        DateTimeOffset? nextUpdate = null;
        if (single.HasData && single.PeekTag().HasSameClassAndValue(Context0))
        {
            var wrapper = single.ReadSequence(Context0);
            nextUpdate = wrapper.ReadGeneralizedTime();
            wrapper.ThrowIfNotEmpty();
        }

        var extensions = single.HasData && single.PeekTag().HasSameClassAndValue(Context1)
            ? ReadExtensions(single, Context1)
            : [];
        single.ThrowIfNotEmpty();

        return new OcspSingleResponse(hashAlgorithm, issuerNameHash, issuerKeyHash, serialNumber,
            certStatus, thisUpdate, nextUpdate, extensions);
    }

    private static IReadOnlyList<OcspExtension> ReadExtensions(AsnReader reader, Asn1Tag tag)
    {
        var wrapper = reader.ReadSequence(tag);
        var list = wrapper.ReadSequence();
        var found = new List<OcspExtension>();

        while (list.HasData)
        {
            var extension = list.ReadSequence();
            var oid = extension.ReadObjectIdentifier();

            // critical is DEFAULT FALSE, so an absent one is not an error and a present one
            // has to be looked for rather than read.
            var critical = extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)
                           && extension.ReadBoolean();

            found.Add(new OcspExtension(oid, critical, extension.ReadOctetString()));
            extension.ThrowIfNotEmpty();
        }

        wrapper.ThrowIfNotEmpty();

        return found;
    }

    private static string ReadAlgorithm(AsnReader algorithm)
    {
        var oid = algorithm.ReadObjectIdentifier();

        // parameters are OPTIONAL. SHA-1 sends an explicit NULL here; ECDSA sends nothing, and
        // a SHA-2 algorithm identifier conventionally omits it. Take whatever is there.
        if (algorithm.HasData)
        {
            algorithm.ReadEncodedValue();
        }

        algorithm.ThrowIfNotEmpty();

        return oid;
    }
}
