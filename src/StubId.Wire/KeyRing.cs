using System.Security.Cryptography.X509Certificates;

namespace StubId.Wire;

/// <summary>
/// The keys one issuer signs with, in the order they appear in its JWKS.
/// </summary>
/// <remarks>
/// Order is not cosmetic. Clients cache metadata for hours, so a key set that reshuffles or
/// regenerates between restarts produces signature failures across every integrating team at
/// once, with nothing on their side to explain it. Keys are therefore loaded, not generated,
/// wherever a restart is expected to be invisible.
/// </remarks>
public sealed class KeyRing : IDisposable
{
    private readonly List<SigningKey> _keys;

    public KeyRing(IEnumerable<SigningKey> keys)
    {
        _keys = [.. keys];

        if (_keys.Count == 0)
        {
            throw new ArgumentException(
                "A key ring needs at least one key: a client that fetches an empty JWKS fails "
                + "with a key-resolution error rather than anything that points here.",
                nameof(keys));
        }
    }

    public IReadOnlyList<SigningKey> Keys => _keys;

    public SigningKey Signing => _keys.First(k => k.Use == KeyUse.Signing);

    public SigningKey ByKid(string kid) =>
        _keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No key with kid {kid}.");

    /// <summary>
    /// Loads keys from PKCS#12 blobs. This is the path a container takes at startup, so it
    /// stays free of key generation: creating RSA keys is the slowest thing a boot can do.
    /// </summary>
    public static KeyRing Load(IEnumerable<(byte[] Pkcs12, string? Password, KeyUse Use)> material) =>
        new(material.Select(m => new SigningKey(
            X509CertificateLoader.LoadPkcs12(
                m.Pkcs12,
                m.Password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet),
            m.Use)));

    public string ToJwks() => JwksWriter.Write(_keys);

    public void Dispose()
    {
        foreach (var key in _keys)
        {
            key.Dispose();
        }
    }
}
