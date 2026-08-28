using System.Security.Cryptography;
using System.Text;

namespace StubId.Wire;

/// <summary>
/// Name-based UUIDs, RFC 9562 section 5.5.
/// </summary>
/// <remarks>
/// Used for the organisation-scoped subject identifier. The broker gives the same person a
/// different <c>sub</c> for each receiving organisation while their MitID UUID stays the
/// same, and deriving it means the value survives a restart instead of being regenerated
/// into every client's cache. .NET has no built-in factory for version 5.
/// </remarks>
public static class Uuid5
{
    public static Guid Create(Guid @namespace, string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        WriteBigEndian(@namespace, namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        Span<byte> input = stackalloc byte[16 + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input[16..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        var result = hash[..16];
        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // variant RFC 4122

        return ReadBigEndian(result);
    }

    // Guid's own byte order is little-endian for the first three fields; the RFC is not.
    private static void WriteBigEndian(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination);
        destination[..4].Reverse();
        destination[4..6].Reverse();
        destination[6..8].Reverse();
    }

    private static Guid ReadBigEndian(Span<byte> bytes)
    {
        Span<byte> ordered = stackalloc byte[16];
        bytes.CopyTo(ordered);
        ordered[..4].Reverse();
        ordered[4..6].Reverse();
        ordered[6..8].Reverse();
        return new Guid(ordered);
    }
}
