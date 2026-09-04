using System.Security.Cryptography.X509Certificates;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// The certificate StubID serves TLS with, when it is asked to serve TLS at all.
/// </summary>
/// <remarks>
/// Off unless configured, because the thing an emulator is for is being reached quickly, and a
/// transport nobody trusts yet is a slower first hour. When it is on, the certificate is written
/// down and loaded afterwards for the same reason the signing keys are: a client that has completed
/// one handshake and pinned what it saw gets a different answer after a restart, and the failure it
/// reports is about trust rather than about the restart.
/// <para>
/// StubID never ships a certificate. One generated here exists on the machine that generated it and
/// nowhere else, which is the only honest way to hand out something whose private key would
/// otherwise have to travel with it.
/// </para>
/// </remarks>
public sealed class ServerCertificate : IDisposable
{
    private const string Password = "stubid";

    private ServerCertificate(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// The configured certificate, or null when TLS is off.
    /// </summary>
    /// <remarks>
    /// Read before the host is built, because Kestrel has to be told what to listen on before it
    /// starts listening. That is why this is a static factory over configuration rather than
    /// something resolved from services.
    /// </remarks>
    public static ServerCertificate? Load(IConfiguration configuration)
    {
        var mode = configuration["StubId:Tls"];

        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "self-signed" => new ServerCertificate(SelfSigned(configuration)),
            "pkcs12" => new ServerCertificate(Supplied(configuration)),
            _ => throw new InvalidOperationException(
                $"StubId:Tls is '{mode}'. It is 'self-signed' for a certificate StubID generates, "
                + "'pkcs12' for one you supply, or unset to serve plain HTTP."),
        };
    }

    /// <summary>The names this certificate will be accepted for.</summary>
    /// <remarks>
    /// Generous on purpose. A container does not know the name its caller will dial - the mapped
    /// port is not the only thing decided outside it - so the certificate covers the loopback names
    /// every local case uses plus the container's own hostname, which is what a sibling on a compose
    /// network resolves. Anything else is configuration, because guessing it is how a certificate
    /// ends up not covering the one name that mattered.
    /// </remarks>
    internal static IReadOnlyList<string> Names(IConfiguration configuration)
    {
        var configured = configuration["StubId:Tls:SubjectAlternativeNames"] ?? "";

        return new[] { "localhost", Environment.MachineName, "127.0.0.1", "::1" }
            .Concat(configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static X509Certificate2 Supplied(IConfiguration configuration)
    {
        var path = configuration["StubId:Tls:Path"]
            ?? throw new InvalidOperationException(
                "StubId:Tls is 'pkcs12', so StubId:Tls:Path has to say where the file is.");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No certificate at '{path}'.");
        }

        return X509CertificateLoader.LoadPkcs12(
            File.ReadAllBytes(path),
            configuration["StubId:Tls:Password"],
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private static X509Certificate2 SelfSigned(IConfiguration configuration)
    {
        var directory = configuration["StubId:KeyPath"]
            ?? Path.Combine(Path.GetTempPath(), "stubid-keys");

        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, "tls.pfx");

        // The same reasoning as the signing keys, and the same failure if it is skipped: two
        // instances sharing a key directory have to serve the same certificate, or a client that
        // pinned what one of them handed out cannot reach the other.
        var stored = WriteOnceFile.ReadOrCreate(file, () =>
        {
            var notBefore = TimeProvider.System.GetUtcNow().AddDays(-1);

            using var created = CertificateFactory.CreateServerCertificate(
                "StubID", Names(configuration), notBefore, notBefore.AddYears(5));

            return created.Export(X509ContentType.Pkcs12, Password);
        });

        return X509CertificateLoader.LoadPkcs12(
            stored,
            Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    public void Dispose() => Certificate.Dispose();
}
