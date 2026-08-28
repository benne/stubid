using StubId.Wire;

namespace StubId.Wire.Tests;

/// <summary>
/// Keys shared across the tests. Generating RSA keys is slow enough that doing it per test
/// would dominate the run.
/// </summary>
public static class TestKeys
{
    private static readonly Lazy<KeyRing> Ring = new(() =>
    {
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = notBefore.AddYears(3);

        return new KeyRing(
        [
            new SigningKey(CertificateFactory.Create("StubID Transaction Signing", notBefore, notAfter), KeyUse.Signing),
            new SigningKey(CertificateFactory.Create("StubID Token Signing", notBefore, notAfter), KeyUse.Signing),
            new SigningKey(CertificateFactory.Create("StubID Request Decryption", notBefore, notAfter), KeyUse.Encryption),
        ]);
    });

    public static KeyRing Keys => Ring.Value;
}
