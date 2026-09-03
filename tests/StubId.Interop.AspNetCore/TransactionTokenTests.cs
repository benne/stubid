extern alias harness;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Wire;
using Signer = harness::StubId.CaptureHarness.RequestObject;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The transaction token StubID emits, compared against the one the broker sent.
/// </summary>
/// <remarks>
/// <para>
/// The same comparison <c>RecordedShapeTests</c> makes of the id_token, the userinfo response
/// and the token body: names, order and JSON types against a recording, since the values are the
/// recorded identity's and cannot match. Separate from that class because a transaction token
/// needs the scope, the broker's own parameters and a second key to exist at all, and because
/// what it asserts grows one recording at a time.
/// </para>
/// <para>
/// The query comes out of the recording rather than being retyped, so a case here cannot claim
/// to reproduce a sitting it no longer resembles.
/// </para>
/// </remarks>
public class TransactionTokenTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";
    private const string Authority = "http://localhost/op";

    // Excluded by the credential guard's own negative lookahead, and useless besides: StubID
    // reads a request object without checking who signed it.
    private const string Password = "not-a-real-secret";

    /// <summary>Fixed, so a signed request is the same bytes on every run.</summary>
    private static readonly DateTimeOffset Issued = new(2026, 9, 2, 9, 45, 0, TimeSpan.Zero);

    /// <summary>Base64 of "StubID transaction text one", which is what CAP-031 sent.</summary>
    private const string RecordedText = "U3R1YklEIHRyYW5zYWN0aW9uIHRleHQgb25l";

    /// <summary>
    /// Every recording StubID can be driven to reproduce. A row is added as each becomes
    /// reachable, and no member is ever filtered out of the comparison.
    /// </summary>
    /// <remarks>
    /// CAP-031 is driven the way it was recorded, through a signed request object, because its
    /// parameters were never in a query: the recorded URL carries <c>client_id</c>,
    /// <c>response_type</c> and a scrubbed <c>request</c>, and the scope, idp_values, idp_params
    /// and prompt are inside the object's payload. Reproducing it unsigned would bake in the one
    /// claim this repository says is unmeasured — that the broker takes a transaction text
    /// without a signed request.
    /// </remarks>
    public static TheoryData<string> EveryRecordingStubIdCanReproduce() =>
        ["CAP-021", "CAP-022", "CAP-031"];

    private readonly HttpClient _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TransactionTokenTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Theory]
    [MemberData(nameof(EveryRecordingStubIdCanReproduce))]
    public async Task The_members_come_in_the_recorded_order(string caseId)
    {
        var emitted = Members(await TransactionTokenFor(caseId));
        var recorded = Members(Recorded(caseId));

        // One assertion over the whole sequence, so a missing member and a reordered one both
        // fail, and the message shows where the two sequences part company.
        Assert.Equal(recorded, emitted);
    }

    [Theory]
    [MemberData(nameof(EveryRecordingStubIdCanReproduce))]
    public async Task Every_member_has_the_recorded_JSON_type(string caseId)
    {
        var emitted = await TransactionTokenFor(caseId);
        var recorded = Recorded(caseId);

        // The traps this catches, each of which produces a token that validates: amr as an
        // array rather than a bare string, auth_time as a number beside the numeric nbf it sits
        // near, transaction_actions as a one-element array, and mitid.age or mitid.has_cpr
        // typed as the number and boolean they look like.
        foreach (var member in recorded.EnumerateObject())
        {
            Assert.Equal(
                member.Value.ValueKind,
                emitted.GetProperty(member.Name).ValueKind);
        }
    }

    [Theory]
    [MemberData(nameof(EveryRecordingStubIdCanReproduce))]
    public async Task The_lifetime_is_the_recorded_six_years(string caseId)
    {
        var emitted = await TransactionTokenFor(caseId);
        var recorded = Recorded(caseId);

        Assert.Equal(
            recorded.GetProperty("exp").GetInt64() - recorded.GetProperty("iat").GetInt64(),
            emitted.GetProperty("exp").GetInt64() - emitted.GetProperty("iat").GetInt64());
    }

    [Fact]
    public async Task auth_time_is_the_id_token_s_instant_rendered_as_a_string()
    {
        // Comparing against iat would prove nothing: under the test host the login and the token
        // fall in the same second, so `iat.ToString()` - the wrong implementation CAP-021 exists
        // to rule out, its auth_time being a second before its iat - would pass. The id_token in
        // the same response carries the same instant as a number, and that is a value with a
        // different source, so tying the two together is an assertion with something to say.
        var body = await Token("openid mitid transaction_token");

        var transaction = Payload(body.GetProperty("transaction_token").GetString()!);
        var identity = Payload(body.GetProperty("id_token").GetString()!);

        Assert.Equal(JsonValueKind.String, transaction.GetProperty("auth_time").ValueKind);
        Assert.Equal(JsonValueKind.Number, identity.GetProperty("auth_time").ValueKind);
        Assert.Equal(
            identity.GetProperty("auth_time").GetInt64().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            transaction.GetProperty("auth_time").GetString());
    }

    /// <summary>
    /// The scope CAP-022 and CAP-031 were both taken with, and the members neither carries.
    /// </summary>
    /// <remarks>
    /// CAP-021's scope turns every conditional branch on, so on its own it cannot tell a rule
    /// that emits these when the scope asks from one that emits them always. CAP-022 closes half
    /// of that as a theory row; this closes the rest, member by member, so a failure names the
    /// one that leaked rather than printing two sequences to compare by eye.
    /// </remarks>
    public static TheoryData<string> NoRecordingCarriesTheseAtTheMinimalScope() =>
    [
        "dk.cpr", "nemid.pid", "nemid.pid_status", "ssn.details.status",
        "mitid.cpr_consent_text", "mitid.cpr_consent_header",

        // The six the transaction text brings. Nothing sent one here, so a rule that emitted
        // them on the scope alone - the scope CAP-031 also carried - would leak all six.
        "mitid.transaction_text", "mitid.transaction_text_type", "mitid.transaction_text_sha256",
        "transaction_text", "transaction_text_type", "transaction_text_sha256",
    ];

    [Theory]
    [MemberData(nameof(NoRecordingCarriesTheseAtTheMinimalScope))]
    public async Task A_minimal_scope_leaves_out_every_conditional_member(string member)
    {
        var emitted = Members(await TransactionToken("openid mitid transaction_token"));

        Assert.DoesNotContain(member, emitted);
    }

    [Fact]
    public async Task transaction_actions_is_a_bare_string_when_there_is_one_action()
    {
        // The member changes JSON type with its length, which is the broker's inconsistency and
        // not a choice made here: CAP-022 sends "mitid.login" and CAP-021 sends a two-element
        // array. Comparing the recorded ValueKind cannot see this - CAP-021's is an Array
        // whatever length StubID emits - so the bare-string form needs its own assertion, and
        // building a list and serialising it is the mistake that would otherwise ship.
        var minimal = await TransactionToken("openid mitid transaction_token");
        var actions = minimal.GetProperty("transaction_actions");

        Assert.Equal(JsonValueKind.String, actions.ValueKind);
        Assert.Equal("mitid.login", actions.GetString());

        var withCpr = await TransactionToken(
            "openid mitid ssn userinfo_token transaction_token");

        Assert.Equal(JsonValueKind.Array, withCpr.GetProperty("transaction_actions").ValueKind);
        Assert.Equal(
            ["mitid.login", "mitid.cpr_match"],
            withCpr.GetProperty("transaction_actions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_pushed_reference_text_survives_the_push()
    {
        // The reason the extraction lives in Parse rather than beside the session. The PAR
        // handler parses its form and returns without ever reaching the code that creates a
        // session, so a push is the path where a parameter read anywhere later is already gone -
        // and the redirect that redeems the reference carries client_id and request_uri and
        // nothing else, so there is no second chance to read it off the query.
        using var pushed = await _client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new("client_id", CodeClient),
            new("client_secret", "any"),
            new("response_type", "code"),
            new("redirect_uri", RedirectUri),
            new("scope", "openid mitid transaction_token"),
            new("nonce", "n"),
            new("idp_values", "mitid"),
            new("idp_params", """{"mitid":{"reference_text":"U3R1YklEIHJlZmVyZW5jZSB0ZXh0"}}"""),
        ]), Ct);

        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        using var reference = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));
        var requestUri = reference.RootElement.GetProperty("request_uri").GetString()!;

        using var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}"
            + $"&request_uri={Uri.EscapeDataString(requestUri)}", Ct);

        var code = System.Web.HttpUtility.ParseQueryString(
            authorize.Headers.Location!.ToString().Split('?')[1])["code"]!;

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", RedirectUri),
            new("client_id", CodeClient),
            new("client_secret", "any"),
        ]), Ct);

        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        var payload = Payload(body.RootElement.GetProperty("transaction_token").GetString()!);

        Assert.Equal(
            "U3R1YklEIHJlZmVyZW5jZSB0ZXh0",
            payload.GetProperty("mitid.reference_text").GetString());
    }

    [Fact]
    public async Task A_request_that_did_not_ask_for_the_scope_gets_neither_member()
    {
        var body = await Token("openid mitid");

        Assert.False(body.TryGetProperty("transaction_token", out _));
        Assert.False(body.TryGetProperty("transaction_token_ocsp_resp", out _));
    }

    [Fact]
    public async Task The_token_and_its_OCSP_response_arrive_together_and_last()
    {
        // No recorded body carries one without the other, and both come after userinfo_token.
        var body = await Token("openid mitid transaction_token");

        Assert.Equal(
            ["id_token", "access_token", "expires_in", "token_type", "scope", "userinfo_token",
                "transaction_token", "transaction_token_ocsp_resp"],
            body.EnumerateObject().Select(m => m.Name));
    }

    [Fact]
    public async Task The_OCSP_response_is_standard_base64_and_says_good_about_the_signing_key()
    {
        var body = await Token("openid mitid transaction_token");
        var served = body.GetProperty("transaction_token_ocsp_resp").GetString()!;

        // Standard base64, where every other encoded value in the same response is base64url.
        // Decoding it as base64url is the mistake this catches, and on most inputs it succeeds.
        var response = harness::StubId.CaptureHarness.Ocsp.Read(Convert.FromBase64String(served));

        Assert.Equal(0, response.ResponseStatus);
        Assert.Equal("1.3.6.1.5.5.7.48.1.1", response.ResponseType);

        var single = Assert.Single(response.Responses);
        Assert.Equal("good", single.CertStatus);

        // The answer is about the key that signed the token beside it. The kid is read out of
        // the token's own header and the certificate out of the JWKS, so an answer about some
        // other certificate fails here rather than passing as a well-formed response.
        var kid = JsonDocument.Parse(
                Base64Url.Decode(body.GetProperty("transaction_token").GetString()!.Split('.')[0]))
            .RootElement.GetProperty("kid").GetString()!;

        using var certificate = await SigningCertificate(kid);

        Assert.True(harness::StubId.CaptureHarness.Ocsp.Matches(single, certificate),
            "The OCSP response does not name the certificate that signed the transaction token.");
    }

    /// <summary>
    /// Drives a login with the parameters a recording actually sent, and returns the token
    /// response.
    /// </summary>
    /// <remarks>
    /// The scope and the broker's own parameters are read out of the recorded URL rather than
    /// retyped here. A scope copied into a test drifts from the recording it claims to reproduce
    /// and nothing notices; read from the fixture, the two cannot disagree.
    /// </remarks>
    private Task<JsonElement> TokenFor(string caseId)
    {
        var (parameters, signed) = RecordedRequest(caseId);
        return Token(parameters, signed);
    }

    /// <summary>Drives a full login and returns the parsed token response.</summary>
    private async Task<JsonElement> Token(string scope) =>
        await Token(new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = scope });

    private async Task<JsonElement> Token(IReadOnlyDictionary<string, string> recorded, bool signed = false)
    {
        var verifier = Base64Url.Encode(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(verifier)));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = CodeClient,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["state"] = "abc",
            ["nonce"] = "n-0S6_WzA2Mj",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        foreach (var (name, value) in recorded)
        {
            parameters[name] = value;
        }

        // A signed sitting is driven the way it was recorded: the object carries everything and
        // the query names the client, the response type and the object. The writer is the one
        // that produced the recording's own request object.
        var query = signed
            ? $"client_id={CodeClient}&response_type=code&request="
              + Uri.EscapeDataString(Signer.Build(parameters, CodeClient, Authority, Password, Issued))
            : string.Join('&', parameters.Select(
                p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        using var authorize = await _client.GetAsync($"/op/connect/authorize?{query}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

        var returned = System.Web.HttpUtility.ParseQueryString(
            authorize.Headers.Location!.ToString().Split('?')[1]);

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", returned["code"]!),
            new("redirect_uri", RedirectUri),
            new("code_verifier", verifier),
            new("client_id", CodeClient),
            new("client_secret", "any-secret-the-existing-configuration-carries"),
        ]), Ct);

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        return JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<JsonElement> TransactionToken(string scope) =>
        Payload((await Token(scope)).GetProperty("transaction_token").GetString()!);

    private async Task<JsonElement> TransactionTokenFor(string caseId) =>
        Payload((await TokenFor(caseId)).GetProperty("transaction_token").GetString()!);

    /// <summary>The claims of a compact JWS, without verifying it.</summary>
    private static JsonElement Payload(string compact) =>
        JsonDocument.Parse(Base64Url.Decode(compact.Split('.')[1])).RootElement.Clone();

    private async Task<System.Security.Cryptography.X509Certificates.X509Certificate2>
        SigningCertificate(string kid)
    {
        using var jwks = JsonDocument.Parse(
            await _client.GetStringAsync("/op/.well-known/openid-configuration/jwks", Ct));

        var key = jwks.RootElement.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("kid").GetString() == kid);

        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(key.GetProperty("x5c").EnumerateArray().First().GetString()!));
    }

    private static IEnumerable<string> Members(JsonElement payload) =>
        payload.EnumerateObject().Select(m => m.Name);

    /// <summary>
    /// What the sitting sent, taken from the recorded authorize URL: the scope and the broker's
    /// own parameters. The client, redirect and PKCE are StubID's, since the recorded ones name
    /// a client StubID does not have and a challenge whose verifier was never written down.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Parameters, bool Signed) RecordedRequest(
        string caseId)
    {
        var callback = Path.Combine(
            RepositoryRoot(), "fixtures", "neb", "pp-session", caseId, "callback");

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(callback, "meta.json")));
        var url = meta.RootElement.GetProperty("request").GetProperty("url").GetString()!;

        // A signed sitting's parameters were never in the URL: what is recorded there is a
        // placeholder, because a compact JWS must not reach a fixture. The payload beside it is
        // where they live, and reading them from there is what lets the recording be driven at
        // all rather than approximated.
        if (url.Contains(Signer.Placeholder, StringComparison.Ordinal))
        {
            using var payload = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(callback, "request_object.payload.json")));
            var root = payload.RootElement.Clone();

            return (Wanted(name =>
                root.TryGetProperty(name, out var claim) && claim.ValueKind == JsonValueKind.String
                    ? claim.GetString()
                    : null), true);
        }

        var query = System.Web.HttpUtility.ParseQueryString(url.Split('?')[1]);

        return (Wanted(name => query[name]), false);
    }

    /// <summary>The four the sitting chose. The rest is StubID's, or the JWT's own furniture.</summary>
    private static IReadOnlyDictionary<string, string> Wanted(Func<string, string?> read) =>
        new[] { "scope", "idp_values", "idp_params", "prompt" }
            .Where(key => read(key) is { Length: > 0 })
            .ToDictionary(key => key, key => read(key)!, StringComparer.Ordinal);

    private static JsonElement Recorded(string caseId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "fixtures", "neb", "pp-session", caseId, "token",
            "transaction_token.payload.json"))).RootElement.Clone();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [Fact]
    public async Task The_recorded_signing_actions_come_back_by_value()
    {
        // The type theory sees only that transaction_actions is an Array. An array with the
        // wrong entries, or the right entries in the wrong order, passes it - and the whole
        // reason this member is interesting is what is inside it.
        var emitted = await TransactionTokenFor("CAP-031");
        var recorded = Recorded("CAP-031");

        Assert.Equal(
            recorded.GetProperty("transaction_actions").EnumerateArray()
                .Select(a => a.GetString()),
            emitted.GetProperty("transaction_actions").EnumerateArray()
                .Select(a => a.GetString()));
    }

    [Fact]
    public async Task The_digest_is_over_the_decoded_text_and_matches_the_recording()
    {
        // The wrong answer a stub reaches for first is the digest of the base64 it was handed.
        // Both are computed here, so a failure says which one was emitted rather than printing
        // two hashes.
        var emitted = await TransactionTokenFor("CAP-031");

        var sent = emitted.GetProperty("mitid.transaction_text").GetString()!;
        var overDecoded = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            Convert.FromBase64String(sent)));
        var overTheBase64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sent)));

        var digest = emitted.GetProperty("mitid.transaction_text_sha256").GetString();

        Assert.Equal(Recorded("CAP-031").GetProperty("mitid.transaction_text_sha256").GetString(), digest);
        Assert.Equal(overDecoded, digest);
        Assert.NotEqual(overTheBase64, digest);

        // Standard base64 rather than base64url, and padded: the OCSP response beside it is the
        // only other value in this response encoded that way.
        Assert.EndsWith("=", digest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_value_comes_back_twice_under_both_spellings()
    {
        var emitted = await TransactionTokenFor("CAP-031");

        foreach (var name in new[] { "transaction_text", "transaction_text_type", "transaction_text_sha256" })
        {
            Assert.Equal(
                emitted.GetProperty($"mitid.{name}").GetString(),
                emitted.GetProperty(name).GetString());
        }

        // The spelling the vendor's prose used and the broker does not.
        Assert.DoesNotContain("mitid.transactiontext", Members(emitted));
    }

    [Fact]
    public async Task The_JWS_payload_carries_a_literal_plus_where_the_recording_does()
    {
        // The recorded digest contains a +, and the same broker sends it two different ways: a
        // literal + inside the JWT payload, \u002B inside the userinfo response body. StubID
        // matches both only because JwsWriter and the userinfo writer picked different JSON
        // encoders, and every other test on this surface parses before comparing - so aligning
        // the two encoders would break a recording with nothing turning red. This reads bytes.
        var recorded = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot(), "fixtures", "neb", "pp-session", "CAP-031", "token",
            "transaction_token.payload.json"), Ct);

        Assert.Contains("+", recorded, StringComparison.Ordinal);

        var body = await Token(RecordedRequest("CAP-031").Parameters, signed: true);
        var payload = System.Text.Encoding.UTF8.GetString(
            Base64Url.Decode(body.GetProperty("transaction_token").GetString()!.Split('.')[1]));

        Assert.Contains("\"mitid.transaction_text_sha256\":\"", payload, StringComparison.Ordinal);
        Assert.Contains("+", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// Values a client can put in transaction_text that this cannot turn into bytes.
    /// </summary>
    /// <remarks>
    /// Every one is answered rather than thrown over. A FormatException on this path leaves the
    /// pipeline as an empty 500 - the one answer the broker never gives - and the value is
    /// entirely the client's, reachable without authenticating anything.
    /// </remarks>
    public static TheoryData<string> TextsThatCannotBeDecoded() =>
    [
        "not base64 at all!",
        "====",
        "YQ==x",
        "%%%%",

        // Whitespace, which Convert.FromBase64String skips rather than refusing. Skipped, this
        // decodes - to different bytes than the client's - and the digest comes back looking
        // right and matching nothing.
        "AAA AAA AAA AAA ",
    ];

    [Theory]
    [MemberData(nameof(TextsThatCannotBeDecoded))]
    public async Task A_text_that_cannot_be_decoded_loses_its_digest_and_nothing_else(string text)
    {
        var emitted = await TransactionToken("openid mitid transaction_token", text, "text");

        Assert.Equal(text, emitted.GetProperty("mitid.transaction_text").GetString());
        Assert.Equal("text", emitted.GetProperty("mitid.transaction_text_type").GetString());
        Assert.False(emitted.TryGetProperty("mitid.transaction_text_sha256", out _));
        Assert.False(emitted.TryGetProperty("transaction_text_sha256", out _));

        // Four members, the pairs still whole, and the signing action still there: the text was
        // sent and is carried, and only the value StubID could not compute is missing.
        Assert.Equal(
            ["mitid.transaction_text", "mitid.transaction_text_type",
                "transaction_text", "transaction_text_type"],
            Members(emitted).Where(m => m.Contains("transaction_text", StringComparison.Ordinal)));

        Assert.Equal(
            ["mitid.login", "mitid.transaction_signing"],
            emitted.GetProperty("transaction_actions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_text_with_no_type_gets_four_members_and_no_invented_type()
    {
        // CAP-031 sent both, so this is unrecorded. Emitting nothing is the rule that never
        // invents a value: a null type would break the userinfo endpoint's every-value-is-a-
        // string invariant, and "text" would be StubID answering a question nobody asked it.
        var emitted = await TransactionToken(
            "openid mitid transaction_token", RecordedText, type: null);

        Assert.Equal(
            ["mitid.transaction_text", "mitid.transaction_text_sha256",
                "transaction_text", "transaction_text_sha256"],
            Members(emitted).Where(m => m.Contains("transaction_text", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_empty_text_is_no_text_at_all()
    {
        // The digest of nothing is a real-looking value, and a presence check written as a null
        // check would emit it. A whitespace-only text is the neighbouring case: it decodes to
        // zero bytes rather than failing, so the length guard alone does not cover it.
        foreach (var text in new[] { "", " " })
        {
            var emitted = await TransactionToken("openid mitid transaction_token", text, "text");

            Assert.DoesNotContain(
                "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
                Members(emitted).Select(m => emitted.GetProperty(m).ToString()));

            Assert.False(emitted.TryGetProperty("mitid.transaction_text_sha256", out _));
        }
    }

    [Fact]
    public async Task An_unescaped_plus_reaches_both_transports_intact()
    {
        // Base64 of any real Danish sentence carries a + about every twenty-one characters, and
        // the recorded text happens to carry none - so no fixture can say what happens to one,
        // and the obvious worry is that a client which forgets to escape it gets a digest over
        // something else. Measured here rather than assumed, on both transports: a query string
        // is not form-encoded and ASP.NET Core leaves a literal + alone there, and the form
        // reader behind the push leaves it alone too. Both answer with the digest of the text
        // the client meant.
        //
        // The plus count is a multiple of four on purpose. Had the + been eaten, what arrived
        // would still have decoded - to different bytes - so the failure would have been a
        // confident wrong digest rather than a missing one, which is the shape worth pinning.
        const string Text = "AAA+AAA+AAA+AAA+";

        var section = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["mitid"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["transaction_text"] = Text,
                    ["transaction_text_type"] = "text",
                },
            });

        // Escaped properly, then un-escaped again for the one character this is about.
        var unescaped = Uri.EscapeDataString(section).Replace("%2B", "+", StringComparison.Ordinal);

        var meant = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(Text)));
        var hadThePlusesBeenEaten = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String("AAAAAAAAAAAA")));

        Assert.NotEqual(meant, hadThePlusesBeenEaten);

        using var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid%20transaction_token&state=s&nonce=n"
            + $"&idp_values=mitid&idp_params={unescaped}", Ct);

        var fromQuery = await Redeem(authorize);

        // The same value through the push, whose body really is form-encoded.
        // FormUrlEncodedContent would escape the +, so the body is written out by hand.
        using var pushed = await _client.PostAsync("/op/connect/par", new StringContent(
            $"client_id={CodeClient}&client_secret=any&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid%20transaction_token"
            + $"&idp_values=mitid&idp_params={unescaped}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"), Ct);

        using var reference = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));

        using var redirected = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&request_uri="
            + Uri.EscapeDataString(reference.RootElement.GetProperty("request_uri").GetString()!), Ct);

        var fromForm = await Redeem(redirected);

        foreach (var emitted in new[] { fromQuery, fromForm })
        {
            Assert.Equal(Text, emitted.GetProperty("mitid.transaction_text").GetString());
            Assert.Equal(meant, emitted.GetProperty("mitid.transaction_text_sha256").GetString());
            Assert.NotEqual(
                hadThePlusesBeenEaten,
                emitted.GetProperty("mitid.transaction_text_sha256").GetString());
        }
    }

    /// <summary>Redeems the code an authorize redirect carries and returns the transaction token.</summary>
    private async Task<JsonElement> Redeem(HttpResponseMessage authorize)
    {
        var code = System.Web.HttpUtility.ParseQueryString(
            authorize.Headers.Location!.ToString().Split('?')[1])["code"]!;

        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", RedirectUri),
            new("client_id", CodeClient),
            new("client_secret", "any"),
        ]), Ct);

        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        return Payload(body.RootElement.GetProperty("transaction_token").GetString()!);
    }

    /// <summary>Drives a login carrying a transaction text, in a query rather than an object.</summary>
    private async Task<JsonElement> TransactionToken(string scope, string text, string? type)
    {
        var mitid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transaction_text"] = text,
        };

        if (type is not null)
        {
            mitid["transaction_text_type"] = type;
        }

        var section = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal) { ["mitid"] = mitid });

        var body = await Token(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope"] = scope,
            ["idp_values"] = "mitid",
            ["idp_params"] = section,
        });

        return Payload(body.GetProperty("transaction_token").GetString()!);
    }
}
