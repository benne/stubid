using System.Security.Cryptography;
using System.Text;

namespace StubId.Wire;

/// <summary>
/// The <c>c_hash</c>, <c>at_hash</c> and <c>s_hash</c> claims.
/// </summary>
/// <remarks>
/// ASP.NET Core requires <c>c_hash</c> whenever an id_token arrives through the front
/// channel, so a hybrid-flow client rejects a token that lacks it or gets it wrong. It is
/// the left half of the SHA-256 digest, which is easy to get wrong by taking the whole
/// digest and reads as a signature failure when you do.
/// </remarks>
public static class HashClaims
{
    /// <summary>
    /// Left-most half of the SHA-256 digest of the ASCII value, base64url encoded. The
    /// hash is fixed at SHA-256 because RS256 is the only algorithm the broker signs with.
    /// </summary>
    public static string Compute(string value)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        return Base64Url.Encode(digest.AsSpan(0, digest.Length / 2));
    }
}
