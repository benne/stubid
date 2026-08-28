using System.Buffers.Text;
using System.Text;

namespace StubId.Wire;

/// <summary>
/// Base64url without padding, as JOSE uses it.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[] Decode(string text) => System.Buffers.Text.Base64Url.DecodeFromChars(text);
}
