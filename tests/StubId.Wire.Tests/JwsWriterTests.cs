using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StubId.Wire.Tests;

public class JwsWriterTests
{
    private readonly JwsWriter _writer = new();

    private static JsonElement Payload(string token)
    {
        var payload = Base64Url.Decode(token.Split('.')[1]);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static JsonElement Header(string token)
    {
        var header = Base64Url.Decode(token.Split('.')[0]);
        return JsonDocument.Parse(header).RootElement.Clone();
    }

    [Fact]
    public void Claims_are_written_in_the_order_given()
    {
        // The broker's id_token opens with iss, and member order is part of what a fixture
        // pins. A serialiser that sorted alphabetically would quietly break that.
        var token = _writer.Sign(
        [
            JsonClaim.String("iss", "https://pp.netseidbroker.dk/op"),
            JsonClaim.String("neb_sid", "a-session"),
            JsonClaim.String("sub", "a-subject"),
        ], TestKeys.Keys.Signing);

        Assert.Equal(
            new[] { "iss", "neb_sid", "sub" },
            Payload(token).EnumerateObject().Select(m => m.Name).ToArray());
    }

    [Fact]
    public void Nothing_is_added_that_was_not_asked_for()
    {
        // This is the whole reason the writer exists. Hand a token library a descriptor and
        // it contributes an nbf, and sometimes a jti, because tokens usually have them. The
        // broker's id_token claim list is not ours to extend.
        var token = _writer.Sign(
            [JsonClaim.String("sub", "a-subject")], TestKeys.Keys.Signing);

        var members = Payload(token).EnumerateObject().Select(m => m.Name).ToArray();

        Assert.Equal(["sub"], members);
    }

    [Fact]
    public void A_value_keeps_the_json_type_it_was_given()
    {
        // The broker sends age and has_cpr as strings. A client parsing them as a number or a
        // boolean fails, so nothing may helpfully convert them.
        var token = _writer.Sign(
        [
            JsonClaim.String("mitid.age", "35"),
            JsonClaim.String("mitid.has_cpr", "true"),
            JsonClaim.Number("exp", 1_800_000_000),
            JsonClaim.Boolean("email_verified", true),
            JsonClaim.Strings("amr", "code_app"),
        ], TestKeys.Keys.Signing);

        var payload = Payload(token);

        Assert.Equal(JsonValueKind.String, payload.GetProperty("mitid.age").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("mitid.has_cpr").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("exp").ValueKind);
        Assert.Equal(JsonValueKind.True, payload.GetProperty("email_verified").ValueKind);
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("amr").ValueKind);
    }

    [Fact]
    public void The_header_names_the_key_that_signed_it()
    {
        var key = TestKeys.Keys.Signing;
        var header = Header(_writer.Sign([JsonClaim.String("sub", "s")], key));

        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal(key.Kid, header.GetProperty("kid").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
    }

    [Fact]
    public void The_signature_verifies_with_the_published_key()
    {
        // What every client does, and the only check that matters if the encoding is wrong.
        var key = TestKeys.Keys.Signing;
        var token = _writer.Sign([JsonClaim.String("sub", "a-subject")], key);

        var parts = token.Split('.');
        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        Assert.True(key.PublicKey.VerifyData(
            signed, Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_repeated_claim_name_is_refused()
    {
        // Parsers disagree about which value wins, so a token with a repeated member is never
        // what anyone meant.
        var error = Assert.Throws<ArgumentException>(() => _writer.Sign(
        [
            JsonClaim.String("sub", "one"),
            JsonClaim.String("sub", "two"),
        ], TestKeys.Keys.Signing));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transaction_tokens_can_be_signed_by_a_different_key_than_id_tokens()
    {
        // The broker signs its transaction token with a separate certificate, published in
        // the same key set. Which key signs which token is a per-profile decision, so the
        // writer takes the key rather than choosing one.
        var transaction = TestKeys.Keys.Keys[0];
        var identity = TestKeys.Keys.Keys[1];

        var first = Header(_writer.Sign([JsonClaim.String("sub", "s")], transaction));
        var second = Header(_writer.Sign([JsonClaim.String("sub", "s")], identity));

        Assert.NotEqual(first.GetProperty("kid").GetString(), second.GetProperty("kid").GetString());
    }
}
