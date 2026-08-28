using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the recorded JWKS and error responses oblige StubID to emit.
/// </summary>
public class KeyAndErrorContractTests
{
    private static JsonElement Body(string captureId) =>
        JsonDocument.Parse(File.ReadAllText(Repository.Fixture(captureId, "response.raw")))
            .RootElement.Clone();

    private static JsonElement[] Keys() =>
        Body("CAP-002").GetProperty("keys").EnumerateArray().ToArray();

    [Fact]
    public void There_are_two_signing_keys_and_one_encryption_key()
    {
        var uses = Keys().Select(k => k.GetProperty("use").GetString()).ToList();

        Assert.Equal(3, uses.Count);
        Assert.Equal(2, uses.Count(u => u == "sig"));
        Assert.Equal(1, uses.Count(u => u == "enc"));
    }

    [Fact]
    public void No_key_carries_an_alg_member()
    {
        // Serialising a JsonWebKey adds one. Emitting the JWKS from a template is what keeps
        // it out.
        Assert.All(Keys(), key => Assert.False(key.TryGetProperty("alg", out _)));
    }

    [Fact]
    public void Every_kid_is_the_uppercase_thumbprint_of_its_certificate()
    {
        foreach (var key in Keys())
        {
            var kid = key.GetProperty("kid").GetString();
            var chain = key.GetProperty("x5c");

            Assert.Equal(1, chain.GetArrayLength());
            Assert.Matches("^[0-9A-F]{40}$", kid);

            using var certificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(chain[0].GetString()!));
            Assert.Equal(certificate.Thumbprint, kid, ignoreCase: true);
        }
    }

    [Fact]
    public void The_transaction_signing_certificate_is_published_in_the_jwks()
    {
        // Worth stating plainly, because the broker's own documentation says otherwise. It
        // lists a thumbprint for this certificate that no longer resolves - the certificate
        // rotated in May 2026 - which reads as "the key is not in the JWKS". Decoding the
        // published chain shows it is. StubID publishes its equivalent key rather than
        // hiding it, so a client following the broker's documented verification path (kid ->
        // JWKS -> x5c) works against both.
        var subjects = Keys().Select(key =>
        {
            using var certificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(key.GetProperty("x5c")[0].GetString()!));
            return certificate.Subject;
        }).ToList();

        Assert.Contains(subjects, s => s.Contains("Transact", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(subjects, s => s.Contains("Token Signing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_error_catalogue_uses_a_pascal_case_envelope()
    {
        // The broker's own OpenAPI document declares these camelCase. The wire disagrees, and
        // the wire wins: a stub generated from the specification would be wrong on day one.
        var catalogue = Body("CAP-007");

        Assert.True(catalogue.TryGetProperty("Version", out _));
        Assert.True(catalogue.TryGetProperty("ErrorCodes", out _));
    }

    [Fact]
    public void Error_codes_carry_only_the_members_they_have()
    {
        // Not every code has a Danish translation or an owning identity provider. Emitting a
        // uniform shape would be a fidelity bug.
        var codes = Body("CAP-007").GetProperty("ErrorCodes").EnumerateObject().ToList();
        var memberCounts = codes.Select(c => c.Value.EnumerateObject().Count()).ToList();

        Assert.NotEmpty(codes);
        Assert.Contains(memberCounts, count => count == 1);
        Assert.DoesNotContain(memberCounts, count => count > 2);
        Assert.Contains(codes, c => c.Name == "mitid_user_aborted");
    }

    [Theory]
    [InlineData("CAP-014")]
    [InlineData("CAP-015")]
    [InlineData("CAP-016")]
    [InlineData("CAP-019")]
    public void Token_errors_say_nothing_beyond_the_error_code(string captureId)
    {
        var body = Body(captureId);

        Assert.True(body.TryGetProperty("error", out _));
        Assert.False(body.TryGetProperty("error_description", out _));
        Assert.False(body.TryGetProperty("error_uri", out _));
    }

    [Fact]
    public void Userinfo_and_cpr_match_challenge_differently()
    {
        // Two endpoints on one host with two different challenges. An emulator that emits one
        // string everywhere would look right until someone asserted on it.
        var userinfo = Challenge("CAP-017");
        var cprMatch = Challenge("CAP-018");

        Assert.Equal("Bearer realm=\"IdentityServer\",error=\"invalid_token\"", userinfo);
        Assert.Equal("Bearer", cprMatch);

        static string Challenge(string captureId) => File
            .ReadAllLines(Repository.Fixture(captureId, "response.head"))
            .First(l => l.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase))
            ["WWW-Authenticate:".Length..].Trim();
    }

    [Fact]
    public void An_invalid_authorize_request_is_never_redirected_back_to_the_client()
    {
        // The client is told nothing at all. Whatever StubID does here, it must not helpfully
        // redirect an error back, or an integration will look correct against the stub and
        // hang against the broker.
        var location = File.ReadAllLines(Repository.Fixture("CAP-008", "response.head"))
            .First(l => l.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("/op/Error?errorId=", location, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5099", location, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_inside_idp_params_are_not_validated_at_the_authorize_endpoint()
    {
        // A malformed uuid_hint is accepted here and only fails later in the MitID flow,
        // which is why the broker publishes a mitid_uuid_hint_malformed code at all. Found by
        // recording it: a plausible-looking stub would have rejected it up front.
        var location = File.ReadAllLines(Repository.Fixture("CAP-010", "response.head"))
            .First(l => l.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("/op/Account/Login", location, StringComparison.Ordinal);
    }
}
