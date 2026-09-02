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
    /// Two signing keys and one for decryption, matching the recorded key set. The first is
    /// the broker's transaction-signing key, published alongside the ordinary token signing
    /// key rather than hidden - so StubID publishes its equivalent, and signs nothing with it
    /// until it issues the token that key is for. See docs/brokers/neb/divergences.md.
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

            // Written through a temporary file and moved into place, so nothing sharing a key
            // directory can read a half-written one or fight over the same handle. Whoever
            // loses the race keeps the winner's key, which is the point: a key that differed
            // per caller would defeat the reason for storing it at all.
            //
            // The temporary name is unique per attempt, not per process. Naming it after the
            // process meant every start inside one process wrote the same temporary file, so
            // one could be moved into place while another was still writing it. Whether that
            // is what made the race test fail once is unproven - it did not reproduce in
            // eight further suite runs or in nineteen hundred concurrent starts - but two
            // writers sharing one path is wrong however narrow the window is.
            var pending = Path.Combine(directory, $"{name}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(pending, created.Export(X509ContentType.Pkcs12, Password));

            try
            {
                File.Move(pending, file, overwrite: false);
            }
            catch (IOException)
            {
                File.Delete(pending);
            }
        }

        return new SigningKey(
            X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(file), Password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet),
            use);
    }

    public void Dispose() => Ring.Dispose();
}
