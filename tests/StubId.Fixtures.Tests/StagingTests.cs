using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

public class TokenFixtureTests
{
    private static string Jws(string header, string payload)
    {
        static string Segment(string json) =>
            System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        return $"{Segment(header)}.{Segment(payload)}.c2lnbmF0dXJl";
    }

    [Fact]
    public void A_token_is_replaced_by_a_placeholder_and_the_rest_of_the_document_is_untouched()
    {
        // The surrounding response keeps its member order, spacing and member positions,
        // which is what a recording of a token response is for. Scrubbing inside the token
        // would invalidate its signature, and re-signing would produce bytes the broker never
        // sent.
        var token = Jws("""{"alg":"RS256","kid":"ABC"}""", """{"sub":"a-subject"}""");
        var body = $$"""{"id_token":"{{token}}","token_type":"Bearer","expires_in":10800}""";

        var (rewritten, tokens) = TokenFixtures.Extract(body);

        Assert.Equal("""{"id_token":"{{ID_TOKEN}}","token_type":"Bearer","expires_in":10800}""", rewritten);
        Assert.Equal("RS256", tokens["id_token"].Algorithm);
        Assert.Equal("ABC", tokens["id_token"].Kid);
    }

    [Fact]
    public void The_decoded_halves_are_kept_verbatim()
    {
        // Member order inside the token is the entire evidence for what the broker sends, so
        // the decoded bytes are stored as they were rather than parsed and written back out.
        const string header = """{"typ":"JWT","alg":"RS256"}""";
        const string payload = """{"iss":"https://example","nbf":1,"sub":"x"}""";

        var (_, tokens) = TokenFixtures.Extract($$"""{"id_token":"{{Jws(header, payload)}}"}""");

        Assert.Equal(header, tokens["id_token"].Header);
        Assert.Equal(payload, tokens["id_token"].Payload);
    }

    [Fact]
    public void The_real_segment_lengths_survive_the_placeholder()
    {
        var token = Jws("""{"alg":"RS256"}""", """{"sub":"x"}""");
        var (_, tokens) = TokenFixtures.Extract($$"""{"id_token":"{{token}}"}""");

        Assert.Equal(token.Split('.').Select(p => p.Length), tokens["id_token"].SegmentLengths);
    }

    [Fact]
    public void A_body_that_is_not_json_is_returned_unchanged()
    {
        const string html = "<html><body>not json</body></html>";

        var (rewritten, tokens) = TokenFixtures.Extract(html);

        Assert.Equal(html, rewritten);
        Assert.Empty(tokens);
    }
}

public class StagingTests
{
    [Fact]
    public void The_same_value_always_becomes_the_same_placeholder()
    {
        // Whether the session identifier in one token equals the one in another is a question
        // the sitting pays an authentication to answer. A fresh pseudonym per occurrence would
        // destroy the answer while looking careful.
        var staging = new Staging();

        var first = staging.Discover("SID", "a-session-identifier");
        var again = staging.Discover("SID", "a-session-identifier");
        var other = staging.Discover("SID", "a-different-identifier");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void A_discovered_value_is_replaced_wherever_it_appears_including_encoded()
    {
        var staging = new Staging();
        staging.Discover("CODE", "the/authorization/code");

        Assert.Equal("code={{CODE_1}}", staging.Scrub("code=the/authorization/code"));
        Assert.Equal("code={{CODE_1}}", staging.Scrub("code=the%2Fauthorization%2Fcode"));
    }

    [Fact]
    public void Values_are_replaced_longest_first()
    {
        // Otherwise a shorter value that is a substring of a longer one rewrites part of it
        // and leaves the remainder exposed.
        var staging = new Staging();
        staging.Discover("TOKEN", "abcdefgh-suffix");
        staging.Discover("TOKEN", "abcdefgh");

        var scrubbed = staging.Scrub("value=abcdefgh-suffix");

        Assert.DoesNotContain("abcdefgh", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_reads_the_values_worth_hiding_out_of_a_token_response()
    {
        var staging = new Staging();
        staging.DiscoverIn("""{"access_token":"an-access-token-value","token_type":"Bearer"}""");

        Assert.DoesNotContain(
            "an-access-token-value",
            staging.Scrub("""{"access_token":"an-access-token-value"}"""),
            StringComparison.Ordinal);
    }
}

public class RedactionParsingTests
{
    [Fact]
    public void A_comment_is_not_a_redaction_rule()
    {
        // The example file carries its guidance under "//" keys. Treating one as a rule made
        // the scrubber replace the comment's own text wherever it appeared, and put a bogus
        // entry in every preflight report.
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "//": "Anything else that must not reach a fixture.",
              "{{ORGANISATION_CVR}}": "12345678"
            }
            """);

        var rules = StubId.CaptureHarness.LocalSettings.ParseRedactions(document.RootElement);

        Assert.Equal(["{{ORGANISATION_CVR}}"], rules.Keys);
    }
}

/// <summary>
/// What a sitting actually writes, for the one step that has never written anything.
/// </summary>
/// <remarks>
/// The canary dry-run proved the scrubbing on steps that send their parameters in the query.
/// The signed step sends a compact JWS of our own making in that query instead, and until
/// CAP-031 is recorded nothing has taken that path as far as a file. A signed token has
/// reached a fixture twice in this project's history; this is the rehearsal for the third
/// way in.
/// </remarks>
public class StagingWriteTests
{
    // Excluded by the credential guard's own negative lookahead, and useless besides.
    private const string Password = "not-a-real-secret";

    [Fact]
    public void The_signed_step_writes_no_token_and_no_personal_number_anywhere()
    {
        var written = WithCredentials(Record);

        // The evidence the step exists for: what travelled inside the object, beside the
        // placeholder that holds its position in the URL.
        Assert.Contains("CAP-031/callback/request_object.payload.json", written.Keys);
        Assert.Contains("transaction_text", written["CAP-031/callback/request_object.payload.json"],
            StringComparison.Ordinal);
        Assert.DoesNotContain("request=ey", written["CAP-031/callback/meta.json"],
            StringComparison.Ordinal);

        Assert.Contains("{{TRANSACTION_TOKEN}}", written["CAP-031/token/response.raw"],
            StringComparison.Ordinal);

        // The whole point. Every file, not the ones we expected to be interesting.
        Assert.All(written, file =>
        {
            Assert.False(SensitiveContent.FindSignedToken(file.Value).Found,
                $"{file.Key} carries a signed token");
            Assert.False(SensitiveContent.FindCpr(file.Value).Found,
                $"{file.Key} carries something shaped like a personal number");
        });
    }

    [Fact]
    public void Nothing_is_left_unaccounted_for_before_the_write()
    {
        // /finish refuses on anything Suspicious finds, and the operator's only way past it is
        // a query parameter. A signed step that trips it for its own request object would
        // teach them to use that parameter, which is how a safety net stops being one.
        Assert.Empty(WithCredentials(() => Staged(null).Suspicious()));
    }

    [Fact]
    public void A_signature_is_checked_while_there_is_still_a_key_to_check_it_against()
    {
        // TokenFixtures.Verify existed and was called by nothing, so every recording in the
        // pack says null here - and once the broker rotates, null is all it can ever say.
        using var key = RSA.Create(2048);
        var meta = WithCredentials(() => Written(Jwks(key), key)["CAP-031/token/meta.json"]);

        using var document = JsonDocument.Parse(meta);
        var token = document.RootElement.GetProperty("tokens").GetProperty("transaction_token");

        Assert.True(token.GetProperty("SignatureVerified").GetBoolean());
        Assert.Contains("StubID Transact Test", token.GetProperty("Certificate").GetString()!,
            StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(
            document.RootElement.GetProperty("capturedAtUtc").GetString()));
    }

    [Fact]
    public void A_callback_that_lost_its_state_says_so_where_it_will_be_read()
    {
        // A signed step's state travels inside the request object. If the broker does not echo
        // it the sitting still has to take the recording, and the absence is a fact about
        // signed requests rather than a detail to lose in a terminal.
        var staging = new Staging();
        var @case = ManualCatalogue.All.Single(c => c.SignRequest);
        staging.Add(@case, "callback", Exchange("http://localhost:5099/callback", "code=abc"),
            "The callback carried no state.");

        var written = WithCredentials(() => Write(staging));

        Assert.Contains("carried no state", written["CAP-031/callback/meta.json"],
            StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Record() => Written(null, null);

    private static Dictionary<string, string> Written(string? jwks, RSA? key) =>
        Write(Staged(jwks, key));

    /// <summary>The two exchanges CAP-031 produces, with the URL the sitting would really send.</summary>
    private static Staging Staged(string? jwks, RSA? key = null)
    {
        var staging = new Staging(jwks);
        var @case = ManualCatalogue.All.Single(c => c.SignRequest);
        var (url, _, _) = Session.BuildAuthorize(@case);

        staging.Add(@case, "callback", Exchange(url, $"code=a-code\nstate={@case.Id}"));
        staging.Add(@case, "token", Exchange(
            "https://pp.netseidbroker.dk/op/connect/token",
            $$"""{"token_type":"Bearer","transaction_token":"{{TransactionToken(key)}}"}"""));

        return staging;
    }

    private static Dictionary<string, string> Write(Staging staging)
    {
        var root = Path.Combine(Path.GetTempPath(), $"stubid-staging-{Guid.NewGuid():N}");
        try
        {
            staging.WriteAsync(new FixtureStore(root), CancellationToken.None).GetAwaiter().GetResult();

            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RecordedExchange Exchange(string url, string body) =>
        new("GET", url, [], null, 200, "OK", [], Encoding.UTF8.GetBytes(body));

    /// <summary>
    /// A transaction token shaped like the one nobody has recorded: RS256, a thumbprint kid,
    /// and the text claims CAP-031 is being run to spell.
    /// </summary>
    private static string TransactionToken(RSA? key)
    {
        static string Segment(string json) =>
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        var header = Segment($$"""{"alg":"RS256","kid":"{{Kid}}","typ":"JWT"}""");
        var payload = Segment(
            """{"mitid.transaction_text":"U3R1YklEIHRyYW5zYWN0aW9uIHRleHQgb25l","auth_time":"1788129644"}""");

        var signature = key is null
            ? "not-a-real-signature-just-enough-to-look-like-one"
            : Base64Url.EncodeToString(key.SignData(
                Encoding.ASCII.GetBytes($"{header}.{payload}"),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

        return $"{header}.{payload}.{signature}";
    }

    /// <summary>A key set carrying that key, with a certificate so the subject can be resolved.</summary>
    private static string Jwks(RSA key)
    {
        var request = new CertificateRequest(
            "CN=StubID Transact Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(60));

        var parameters = key.ExportParameters(includePrivateParameters: false);

        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","kid":"{{Kid}}",
            "n":"{{Base64Url.EncodeToString(parameters.Modulus!)}}",
            "e":"{{Base64Url.EncodeToString(parameters.Exponent!)}}",
            "x5c":["{{Convert.ToBase64String(certificate.RawData)}}"]}]}
            """;
    }

    private const string Kid = "0000000000000000000000000000000000000000";

    /// <summary>
    /// The environment wins over capture.local.json, so this behaves the same on a machine
    /// with real credentials and on one with none, which is what CI is.
    /// </summary>
    private static T WithCredentials<T>(Func<T> act)
    {
        (string Name, string Value)[] settings =
        [
            ("STUBID_NEB_PP_CLIENT_ID", "00000000-0000-0000-0000-000000000000"),
            ("STUBID_NEB_PP_CLIENT_SECRET", Password),
        ];

        var previous = settings.Select(s => Environment.GetEnvironmentVariable(s.Name)).ToArray();
        foreach (var (name, value) in settings)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            return act();
        }
        finally
        {
            for (var i = 0; i < settings.Length; i++)
            {
                Environment.SetEnvironmentVariable(settings[i].Name, previous[i]);
            }
        }
    }
}
