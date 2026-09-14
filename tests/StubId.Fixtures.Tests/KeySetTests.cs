using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What a sitting may say about a token's signature, against a key set that is not one document.
/// </summary>
/// <remarks>
/// The second broker answers each request for its key set with a different subset of its keys.
/// A sitting fetched the set once and called any token whose kid was missing from that one fetch
/// unverified, which would have written a false claim about a genuine token into a fixture.
/// </remarks>
public class KeySetTests : IDisposable
{
    private readonly RSA signer = RSA.Create(2048);

    private readonly RSA other = RSA.Create(2048);

    public void Dispose()
    {
        signer.Dispose();
        other.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Key(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            kty = "RSA",
            use = "sig",
            kid,
            e = Base64Url.EncodeToString(parameters.Exponent),
            n = Base64Url.EncodeToString(parameters.Modulus),
            alg = "RS256",
        });
    }

    private static string KeySet(params string[] keys) => $"{{\"keys\":[{string.Join(',', keys)}]}}";

    private static string Token(RSA rsa, string kid)
    {
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid }));
        var payload = Base64Url.EncodeToString("""{"sub":"a-subject"}"""u8);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
    }

    [Fact]
    public void A_token_verifies_against_the_key_its_kid_names()
    {
        Assert.True(TokenFixtures.Verify(Token(signer, "a"), KeySet(Key(signer, "a"))));
    }

    [Fact]
    public void A_token_signed_by_another_key_under_its_kid_fails()
    {
        Assert.False(TokenFixtures.Verify(Token(other, "a"), KeySet(Key(signer, "a"))));
    }

    [Fact]
    public void A_token_whose_kid_is_not_in_the_set_is_unchecked_rather_than_failed()
    {
        Assert.Null(TokenFixtures.Verify(Token(signer, "c"), KeySet(Key(signer, "a"), Key(other, "b"))));
    }

    [Fact]
    public void Merged_key_sets_hold_each_kid_once_in_the_order_first_seen()
    {
        var first = KeySet(Key(signer, "a"), Key(other, "b"));
        var second = KeySet(Key(signer, "c"), Key(other, "b"));

        var merged = TokenFixtures.MergeKeySets([first, second]);

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(
            ["a", "b", "c"],
            document.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()));
        Assert.Equal(3, TokenFixtures.KeyCount(merged));

        // The point of merging: a token the first fetch could not check, the merged set can.
        Assert.Null(TokenFixtures.Verify(Token(signer, "c"), first));
        Assert.True(TokenFixtures.Verify(Token(signer, "c"), merged));
    }
}
