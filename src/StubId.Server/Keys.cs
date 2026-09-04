using System.Security.Cryptography.X509Certificates;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// The signing keys, kept stable across restarts.
/// </summary>
/// <remarks>
/// Clients cache discovery metadata for hours, so a server that generates fresh keys on
/// every start produces signature failures across every integrating application at once,
/// with nothing on their side to explain it. Keys are therefore written down on first use
/// and loaded afterwards.
/// </remarks>
public sealed class Keys : IDisposable
{
    private const string Password = "stubid";

    public Keys(IConfiguration configuration)
    {
        var path = configuration["StubId:KeyPath"]
            ?? Path.Combine(Path.GetTempPath(), "stubid-keys");

        Directory.CreateDirectory(path);
        Ring = new KeyRing(
        [
            Load(path, "transaction-signing", KeyUse.Signing),
            Load(path, "token-signing", KeyUse.Signing),
            Load(path, "request-decryption", KeyUse.Encryption),
        ]);

        OcspResponder = LoadCertificate(path, "ocsp-responder", (from, to) =>
            CertificateFactory.CreateOcspResponder("StubID ocsp-responder", from, to));
    }

    /// <summary>
    /// Two signing keys and one for decryption, matching the recorded key set. The first is
    /// the broker's transaction-signing key, published alongside the ordinary token signing
    /// key rather than hidden, and it signs the transaction token - the one token in the
    /// response that does not use <see cref="TokenSigning"/>. See
    /// docs/brokers/neb/divergences.md.
    /// </summary>
    public KeyRing Ring { get; }

    public SigningKey TransactionSigning => Ring.Keys[0];

    public SigningKey TokenSigning => Ring.Keys[1];

    /// <summary>
    /// The elliptic-curve certificate that signs the OCSP response beside a transaction token.
    /// </summary>
    /// <remarks>
    /// Deliberately not in <see cref="Ring"/>. The ring is what JWKS publishes, and an OCSP
    /// responder is not a JWKS key: a client finds it inside the response it signed, which is
    /// the only place it belongs. It is kept beside the others so it survives a restart for the
    /// same reason they do.
    /// </remarks>
    public X509Certificate2 OcspResponder { get; }

    private static SigningKey Load(string directory, string name, KeyUse use) =>
        new(LoadCertificate(directory, name, (from, to) =>
            CertificateFactory.Create($"StubID {name}", from, to)), use);

    private static X509Certificate2 LoadCertificate(
        string directory, string name, Func<DateTimeOffset, DateTimeOffset, X509Certificate2> create)
    {
        var file = Path.Combine(directory, $"{name}.pfx");

        // Whoever loses the race keeps the winner's key, which is the point: a key that differed
        // per caller would defeat the reason for storing one at all. WriteOnceFile is what makes
        // that true when several starts share a directory.
        var stored = WriteOnceFile.ReadOrCreate(file, () =>
        {
            // From the clock rather than a literal: a fixed date eventually generates a
            // certificate that is already expired.
            var notBefore = TimeProvider.System.GetUtcNow().AddDays(-1);
            using var created = create(notBefore, notBefore.AddYears(5));

            return created.Export(X509ContentType.Pkcs12, Password);
        });

        return X509CertificateLoader.LoadPkcs12(
            stored, Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    public void Dispose()
    {
        Ring.Dispose();
        OcspResponder.Dispose();
    }
}
