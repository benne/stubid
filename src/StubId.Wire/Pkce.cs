using System.Security.Cryptography;
using System.Text;
using StubId.Abstractions;

namespace StubId.Wire;

/// <summary>
/// Proof Key for Code Exchange verification.
/// </summary>
[Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
    Evidence = "fixtures/neb/pp/CAP-001")]
public static class Pkce
{
    /// <summary>
    /// The broker advertises both <c>S256</c> and <c>plain</c>, so both are accepted. Plain
    /// is weaker, but refusing it here would mean rejecting a request the real broker
    /// accepts.
    /// </summary>
    public static bool Verify(string verifier, string challenge, string method)
    {
        var computed = method switch
        {
            "S256" => Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            "plain" => verifier,
            _ => null,
        };

        return computed is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }
}
