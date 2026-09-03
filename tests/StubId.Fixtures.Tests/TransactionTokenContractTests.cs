using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the recorded transaction tokens oblige StubID to emit when it emits one.
/// </summary>
/// <remarks>
/// <para>
/// Nothing read a recorded transaction token before these, though three have been committed
/// since CAP-021. The discovery document and the JWKS are pinned this way and the token that
/// carries a signature's evidence was not, which left its member types free to be guessed at
/// by whoever writes StubID's own - and the guesses that matter here are the ones that look
/// obviously right: a numeric auth_time, an array of actions, one spelling per claim.
/// </para>
/// <para>
/// These assert the recordings, not the server. What StubID emits is asserted against them by
/// <c>TransactionTokenTests</c> in the interop suite, which drives a real login and compares
/// member names, order and JSON types. Keeping the two apart is the point: if both read the
/// same file, a fixture that drifted would take the server's assertions with it.
/// </para>
/// </remarks>
public class TransactionTokenContractTests
{
    /// <summary>Every recording that carries a transaction token.</summary>
    public static TheoryData<string> EveryRecording() => ["CAP-021", "CAP-022", "CAP-031"];

    /// <summary>The one that signed something, rather than only logging in.</summary>
    private const string Signing = "CAP-031";

    private static JsonElement Payload(string caseId) => Read(caseId, "transaction_token.payload.json");

    private static JsonElement Header(string caseId) => Read(caseId, "transaction_token.header.json");

    private static JsonElement Read(string caseId, string file) =>
        JsonDocument.Parse(File.ReadAllText(Repository.SessionFixture(caseId, "token", file)))
            .RootElement.Clone();

    [Fact]
    public void The_transaction_text_arrives_under_both_spellings()
    {
        // The four-way contradiction in the broker's own documentation resolves as "both at
        // once": three values, six members, underscored every time. A stub that picks one
        // spelling is wrong for half its callers whichever one it picks.
        var payload = Payload(Signing);

        foreach (var member in new[] { "transaction_text", "transaction_text_type", "transaction_text_sha256" })
        {
            Assert.Equal(
                payload.GetProperty($"mitid.{member}").GetString(),
                payload.GetProperty(member).GetString());
        }

        Assert.False(payload.TryGetProperty("mitid.transactiontext", out _),
            "the unpunctuated spelling the documentation also uses is not one this broker sends");
    }

    [Fact]
    public void The_text_comes_back_as_the_base64_that_was_sent()
    {
        // Both halves are committed, so the round trip is checkable rather than asserted: what
        // the request object carried, against what the token returned. Never decoded in transit.
        var sent = JsonDocument.Parse(JsonDocument
            .Parse(File.ReadAllText(
                Repository.SessionFixture(Signing, "callback", "request_object.payload.json")))
            .RootElement.GetProperty("idp_params").GetString()!);

        var asked = sent.RootElement.GetProperty("mitid").GetProperty("transaction_text").GetString();

        Assert.Equal(asked, Payload(Signing).GetProperty("mitid.transaction_text").GetString());
    }

    [Fact]
    public void The_digest_is_base64_of_the_hash_of_the_decoded_text()
    {
        // Two things a stub gets wrong by default and then matches forever: the digest is over
        // the decoded text rather than the base64 that was sent, and it is base64 of the hash
        // rather than hex. The runbook wrote both candidate digests down beforehand for this.
        var payload = Payload(Signing);
        var sent = payload.GetProperty("mitid.transaction_text").GetString()!;
        var decoded = System.Buffers.Text.Base64Url.DecodeFromChars(sent);

        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(decoded)),
            payload.GetProperty("mitid.transaction_text_sha256").GetString());
    }

    [Theory]
    [InlineData("CAP-022", JsonValueKind.String)]
    [InlineData("CAP-021", JsonValueKind.Array)]
    [InlineData("CAP-031", JsonValueKind.Array)]
    public void transaction_actions_is_a_string_alone_and_an_array_in_company(
        string caseId, JsonValueKind kind)
    {
        // One action arrives bare, two arrive as an array. A client reading this member has to
        // handle both, and so does anything generating it.
        Assert.Equal(kind, Payload(caseId).GetProperty("transaction_actions").ValueKind);
    }

    [Fact]
    public void A_signing_transaction_names_itself_among_its_actions()
    {
        var actions = Payload(Signing).GetProperty("transaction_actions")
            .EnumerateArray().Select(a => a.GetString()).ToList();

        Assert.Equal(["mitid.login", "mitid.transaction_signing"], actions);
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void Three_documented_members_are_never_sent(string caseId)
    {
        // Negative facts, which are the ones a stub adds back by accident. The vendor's worked
        // example carries spec_ver and recipient_info; a top-level redirect_uri appears where
        // recipient_info is documented. signing_cert_ocsp_nonce is absent on a login and on a
        // signing transaction, which are the only two flows that could have shown it.
        var payload = Payload(caseId);

        foreach (var member in new[] { "spec_ver", "recipient_info", "signing_cert_ocsp_nonce" })
        {
            Assert.False(payload.TryGetProperty(member, out _), $"{caseId} carries {member}");
        }

        Assert.True(payload.TryGetProperty("redirect_uri", out _));
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void auth_time_is_a_string_beside_time_claims_that_are_numbers(string caseId)
    {
        var payload = Payload(caseId);

        Assert.Equal(JsonValueKind.String, payload.GetProperty("auth_time").ValueKind);

        foreach (var member in new[] { "nbf", "iat", "exp" })
        {
            Assert.Equal(JsonValueKind.Number, payload.GetProperty(member).ValueKind);
        }
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void amr_is_a_bare_string_here_and_an_array_in_every_other_token(string caseId)
    {
        // Same response, same login, two answers. The transaction token is the only one of the
        // four that does not send an array.
        Assert.Equal(JsonValueKind.String, Payload(caseId).GetProperty("amr").ValueKind);

        foreach (var other in new[] { "id_token", "access_token", "userinfo_token" })
        {
            Assert.Equal(JsonValueKind.Array,
                Read(caseId, $"{other}.payload.json").GetProperty("amr").ValueKind);
        }
    }

    [Theory]
    [MemberData(nameof(EveryRecording))]
    public void A_different_published_key_signs_it(string caseId)
    {
        // Asserted rather than assumed, which is what the sitting's step 10 asked for: the kid
        // in the header, resolved against the recorded JWKS, is the Transact certificate and
        // not the one that signs everything else in the same response.
        var kid = Header(caseId).GetProperty("kid").GetString();

        Assert.NotEqual(kid, Read(caseId, "id_token.header.json").GetProperty("kid").GetString());
        Assert.Contains("Transact", SubjectOf(kid!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_response_carries_the_transaction_pair_or_neither()
    {
        // Nine recorded token responses, two shapes, and the scope decides which. The pair is
        // never split: a transaction token without its OCSP response was never served.
        var seen = 0;

        foreach (var file in Directory.EnumerateFiles(Repository.NebSession, "response.raw",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.StartsWith("{\"id_token\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var body = JsonDocument.Parse(text);
            var members = body.RootElement.EnumerateObject().Select(m => m.Name).ToArray();
            var asked = body.RootElement.GetProperty("scope").GetString()!
                .Split(' ').Contains("transaction_token");

            Assert.Equal(
                asked
                    ? ["id_token", "access_token", "expires_in", "token_type", "scope",
                       "userinfo_token", "transaction_token", "transaction_token_ocsp_resp"]
                    : new[] { "id_token", "access_token", "expires_in", "token_type", "scope",
                              "userinfo_token" },
                members);
            seen++;
        }

        // Counted, so that a moved fixture path leaves this failing rather than passing over
        // nothing at all.
        Assert.Equal(9, seen);
    }

    [Fact]
    public void Userinfo_returns_the_digest_without_the_text_it_is_over()
    {
        // The endpoint splits what the token sends whole, and a reference text is not split at
        // all - it comes back there with no type and no digest beside it.
        using var signing = JsonDocument.Parse(File.ReadAllText(
            Repository.SessionFixture(Signing, "userinfo", "response.raw")));
        using var reference = JsonDocument.Parse(File.ReadAllText(
            Repository.SessionFixture("CAP-022", "userinfo", "response.raw")));

        Assert.True(signing.RootElement.TryGetProperty("mitid.transaction_text_type", out _));
        Assert.True(signing.RootElement.TryGetProperty("mitid.transaction_text_sha256", out _));
        Assert.False(signing.RootElement.TryGetProperty("mitid.transaction_text", out _));

        Assert.True(reference.RootElement.TryGetProperty("mitid.reference_text", out _));
        Assert.False(reference.RootElement.TryGetProperty("mitid.reference_text_sha256", out _));
    }

    private static string SubjectOf(string kid)
    {
        using var jwks = JsonDocument.Parse(
            File.ReadAllText(Repository.Fixture("CAP-002", "response.raw")));

        var key = jwks.RootElement.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("kid").GetString() == kid);

        using var certificate = X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(key.GetProperty("x5c")[0].GetString()!));

        return certificate.Subject;
    }
}
