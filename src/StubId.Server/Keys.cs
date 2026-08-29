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
    }

    /// <summary>
    /// Two signing keys and one for decryption, matching the recorded key set. The first
    /// signs transaction tokens, which the broker publishes alongside the ordinary token
    /// signing key rather than hiding.
    /// </summary>
    public KeyRing Ring { get; }

    public SigningKey TransactionSigning => Ring.Keys[0];

    public SigningKey TokenSigning => Ring.Keys[1];

    private static SigningKey Load(string directory, string name, KeyUse use)
    {
        var file = Path.Combine(directory, $"{name}.pfx");

        if (!File.Exists(file))
        {
            // From the clock rather than a literal: a fixed date eventually generates a
            // certificate that is already expired.
            var notBefore = TimeProvider.System.GetUtcNow().AddDays(-1);
            using var created = CertificateFactory.Create($"StubID {name}", notBefore, notBefore.AddYears(5));
            File.WriteAllBytes(file, created.Export(X509ContentType.Pkcs12, Password));
        }

        return new SigningKey(
            X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(file), Password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet),
            use);
    }

    public void Dispose() => Ring.Dispose();
}
