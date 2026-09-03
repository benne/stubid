extern alias harness;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Wire;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The transaction token StubID emits, compared against the one the broker sent.
/// </summary>
/// <remarks>
/// <para>
/// This is the first test in the repository that compares StubID's <em>output</em> against a
/// recording rather than reading the recording alone. Everything under
/// <c>tests/StubId.Fixtures.Tests</c> asserts what the broker did; this asserts that StubID
/// does the same thing, which is a different claim and the one the project is actually for.
/// </para>
/// <para>
/// Member order is part of the contract - <c>docs/brokers/neb/claims.md</c> opens by saying so -
/// and nothing in the suite asserted the order of any token before this. Values differ, because
/// StubID's citizen is not the broker's test identity and its keys are its own. Names, order
/// and JSON types do not.
/// </para>
/// </remarks>
public class TransactionTokenTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    /// <summary>
    /// The scope each recording asked with, so the conditional members are decided by the same
    /// input rather than by a list written here.
    /// </summary>
    /// <remarks>
    /// CAP-022 arrives with the <c>idp_params</c> reader: its token carries
    /// <c>mitid.reference_text</c>, which needs a parameter nothing reads yet. CAP-031 arrives
    /// with the transaction text. Each is a row added to this theory, never a member filtered
    /// out of the comparison.
    /// </remarks>
    public static TheoryData<string, string> EveryRecordingStubIdCanReproduce() => new()
    {
        {
            "CAP-021",
            "openid mitid ssn nemid.pid ssn.details_name ssn.details_address userinfo_token "
            + "transaction_token"
        },
    };

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
    public async Task The_members_come_in_the_recorded_order(string caseId, string scope)
    {
        var emitted = Members(await TransactionToken(scope));
        var recorded = Members(Recorded(caseId));

        // One assertion over the whole sequence, so a missing member and a reordered one both
        // fail, and the message shows where the two sequences part company.
        Assert.Equal(recorded, emitted);
    }

    [Theory]
    [MemberData(nameof(EveryRecordingStubIdCanReproduce))]
    public async Task Every_member_has_the_recorded_JSON_type(string caseId, string scope)
    {
        var emitted = await TransactionToken(scope);
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
    public async Task The_lifetime_is_the_recorded_six_years(string caseId, string scope)
    {
        var emitted = await TransactionToken(scope);
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
    /// CAP-021 is the only theory row above, and its scope turns every conditional branch on -
    /// so on its own it cannot tell a rule that emits these when the scope asks from one that
    /// emits them always. Two recordings settle the negative, and this is where they say so.
    /// </remarks>
    public static TheoryData<string> NoRecordingCarriesTheseAtTheMinimalScope() =>
    [
        "dk.cpr", "nemid.pid", "nemid.pid_status", "ssn.details.status",
        "mitid.cpr_consent_text", "mitid.cpr_consent_header",
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

    /// <summary>Drives a full login and returns the parsed token response.</summary>
    private async Task<JsonElement> Token(string scope)
    {
        var verifier = Base64Url.Encode(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(verifier)));

        using var authorize = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&scope={Uri.EscapeDataString(scope)}"
            + $"&state=abc&nonce=n-0S6_WzA2Mj&code_challenge={challenge}"
            + "&code_challenge_method=S256", Ct);

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
}
