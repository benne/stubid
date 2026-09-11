using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What signs a request object, now that the two brokers do not agree on how.
/// </summary>
/// <remarks>
/// The first broker accepts HS256 over the client secret, measured in
/// docs/research/signed-requests.md. The second advertises nine algorithms and not one of them is
/// symmetric, so the same code cannot reach both with one signer.
/// </remarks>
public class RequestSignerTests
{
    private const string Password = "not-a-real-secret";
    private const string Authority = "https://pp.netseidbroker.dk/op";
    private const string Client = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private static Dictionary<string, string> Parameters() => new(StringComparer.Ordinal)
    {
        ["client_id"] = Client,
        ["response_type"] = "code",
        ["scope"] = "openid mitid",
    };

    /// <summary>
    /// The first broker's header is the bytes it always was.
    /// </summary>
    /// <remarks>
    /// This is the one that protects a recording. CAP-031 stores the decoded header and the
    /// three segment lengths of the object the sitting sent, and a header that gained a member
    /// would contradict a fixture nobody can re-record without spending an authentication.
    /// </remarks>
    [Fact]
    public void A_secret_signed_header_carries_exactly_alg_and_typ()
    {
        var compact = RequestObject.Build(
            Parameters(), Client, Authority, RequestSigner.ClientSecret(Password));

        Assert.Equal("""{"alg":"HS256","typ":"JWT"}""", Decode(compact.Split('.')[0]));
    }

    [Fact]
    public void A_secret_signed_object_verifies_against_the_secret()
    {
        var compact = RequestObject.Build(
            Parameters(), Client, Authority, RequestSigner.ClientSecret(Password));

        var segments = compact.Split('.');
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Password),
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"));

        Assert.Equal(Base64Url.EncodeToString(expected), segments[2]);
    }

    /// <summary>
    /// A key the harness holds signs RS256 and says which key it was.
    /// </summary>
    /// <remarks>
    /// The key identifier is the half that has never been exercised: the extractor has always
    /// read a <c>kid</c> back out of a header and has never once found one, because nothing the
    /// harness signed had a registered key behind it.
    /// </remarks>
    [Fact]
    public void A_private_key_signs_rs256_and_names_itself()
    {
        using var key = RSA.Create(2048);

        var compact = RequestObject.Build(
            Parameters(),
            Client,
            Authority,
            RequestSigner.PrivateKey(key.ExportPkcs8PrivateKeyPem(), "a-registered-key"));

        using var header = JsonDocument.Parse(Decode(compact.Split('.')[0]));

        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("a-registered-key", header.RootElement.GetProperty("kid").GetString());
    }

    [Fact]
    public void A_private_key_signature_verifies_against_the_public_half()
    {
        using var key = RSA.Create(2048);

        var compact = RequestObject.Build(
            Parameters(), Client, Authority,
            RequestSigner.PrivateKey(key.ExportPkcs8PrivateKeyPem(), "k"));

        var segments = compact.Split('.');

        Assert.True(key.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            Base64Url.DecodeFromChars(segments[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    /// <summary>
    /// An elliptic key signs in the format a JWS is read in.
    /// </summary>
    /// <remarks>
    /// Not the format .NET writes by default. A JWS signature is r and s at fixed width; the DER
    /// encoding every default overload produces verifies nowhere and presents as a bad key, which
    /// is the kind of thing a sitting finds out the expensive way.
    /// </remarks>
    [Fact]
    public void An_elliptic_key_signs_in_the_concatenated_form()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var compact = RequestObject.Build(
            Parameters(), Client, Authority,
            RequestSigner.PrivateKey(key.ExportPkcs8PrivateKeyPem(), "k"));

        var segments = compact.Split('.');
        using var header = JsonDocument.Parse(Decode(segments[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());

        var signature = Base64Url.DecodeFromChars(segments[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Something_that_is_not_a_private_key_is_refused_by_name()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => RequestSigner.PrivateKey("-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----", null));

        Assert.Contains("private key", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first broker signs with the client's secret, and needs no key.</summary>
    [Fact]
    public void The_first_broker_signs_with_the_clients_secret()
    {
        var signer = BrokerTarget.NetsEidBroker.SignerFor(
            BrokerClient.NetsEidBroker.OpenCode, _ => Password);

        Assert.Equal("HS256", signer.Algorithm);
        Assert.Null(signer.KeyId);
    }

    /// <summary>
    /// The second refuses rather than falling back to a secret it would sign nothing with.
    /// </summary>
    /// <remarks>
    /// Signing HS256 there would be accepted by nothing and would present as a bad request
    /// object, which is the failure the error page refuses to explain. Refusing by name is the
    /// difference between a setup problem and an afternoon.
    /// </remarks>
    [Fact]
    public void The_second_broker_refuses_without_a_key_and_names_the_setting()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => BrokerTarget.Signicat.SignerFor(
                BrokerClient.NetsEidBroker.OpenCode, _ => null));

        Assert.Contains("STUBID_SIGNICAT_PRIVATE_KEY_PATH", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_second_broker_signs_with_the_key_the_settings_name()
    {
        using var key = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"stubid-signer-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());

        try
        {
            var signer = BrokerTarget.Signicat.SignerFor(
                BrokerClient.NetsEidBroker.OpenCode,
                setting => setting switch
                {
                    "STUBID_SIGNICAT_PRIVATE_KEY_PATH" => path,
                    "STUBID_SIGNICAT_KEY_ID" => "the-registered-key",
                    _ => null,
                });

            Assert.Equal("RS256", signer.Algorithm);
            Assert.Equal("the-registered-key", signer.KeyId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Decode(string segment) =>
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segment));
}
