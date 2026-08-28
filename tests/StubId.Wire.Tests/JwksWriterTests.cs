using System.Text.Json;

namespace StubId.Wire.Tests;

/// <summary>
/// The JWKS is compared against the recorded one structurally: same members, same order,
/// same shapes, differing only in key material.
/// </summary>
public class JwksWriterTests
{
    private static string RecordedJwks() => File.ReadAllText(
        Path.Combine(Repository.Root, "fixtures", "neb", "pp", "CAP-002", "response.raw"));

    /// <summary>
    /// Reduces a document to its structure: member names in order, with values replaced by a
    /// token describing their kind. Two documents with the same skeleton differ only in
    /// values, which for a key set means only in key material.
    /// </summary>
    private static string Skeleton(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new System.Text.StringBuilder();
        Walk(document.RootElement, builder);
        return builder.ToString();

        static void Walk(JsonElement element, System.Text.StringBuilder builder)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    foreach (var member in element.EnumerateObject())
                    {
                        builder.Append(member.Name).Append(':');
                        Walk(member.Value, builder);
                        builder.Append(',');
                    }

                    builder.Append('}');
                    break;

                case JsonValueKind.Array:
                    builder.Append('[');
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, builder);
                        builder.Append(',');
                    }

                    builder.Append(']');
                    break;

                case JsonValueKind.String:
                    builder.Append("<string>");
                    break;

                default:
                    builder.Append('<').Append(element.ValueKind).Append('>');
                    break;
            }
        }
    }

    [Fact]
    public void Our_key_set_has_the_same_structure_as_the_recorded_one()
    {
        var ours = JwksWriter.Write(TestKeys.Keys.Keys);

        Assert.Equal(Skeleton(RecordedJwks()), Skeleton(ours));
    }

    [Fact]
    public void The_document_is_compact_like_the_recorded_one()
    {
        // Member order and the absence of whitespace both survive only because the document
        // is written rather than serialised from an object.
        var ours = JwksWriter.Write(TestKeys.Keys.Keys);

        Assert.DoesNotContain('\n', ours);
        Assert.StartsWith("""{"keys":[{"kty":"RSA","use":"sig","kid":""", ours, StringComparison.Ordinal);
    }

    [Fact]
    public void No_key_carries_an_alg_member()
    {
        // Every JOSE library adds one. The broker publishes none.
        using var document = JsonDocument.Parse(JwksWriter.Write(TestKeys.Keys.Keys));

        Assert.All(
            document.RootElement.GetProperty("keys").EnumerateArray(),
            key => Assert.False(key.TryGetProperty("alg", out _)));
    }

    [Fact]
    public void Kid_is_the_uppercase_thumbprint_and_x5t_is_its_base64url_form()
    {
        var key = TestKeys.Keys.Signing;

        Assert.Matches("^[0-9A-F]{40}$", key.Kid);
        Assert.Equal(key.Certificate.Thumbprint, key.Kid);
        Assert.Equal(Base64Url.Encode(Convert.FromHexString(key.Kid)), key.X5t);
    }

    [Fact]
    public void Each_key_publishes_exactly_one_certificate()
    {
        using var document = JsonDocument.Parse(JwksWriter.Write(TestKeys.Keys.Keys));

        Assert.All(
            document.RootElement.GetProperty("keys").EnumerateArray(),
            key => Assert.Equal(1, key.GetProperty("x5c").GetArrayLength()));
    }
}
