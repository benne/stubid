using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the unattended writer does with a signed token a broker puts in a response header.
/// </summary>
/// <remarks>
/// <para>
/// Signicat's accepted authorize redirects with the whole request inside a token it signed, and
/// that token names the client. The sitting's writer has always split tokens out of bodies; this
/// one wrote headers through a scrub that cannot see into one.
/// </para>
/// <para>
/// The token is built when the test runs rather than written out, because a compact token in
/// this file is exactly what the guard over the repository refuses. In the collection because the
/// writer scrubs, and this sets a setting the scrub reads.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class FixtureStoreTests : IDisposable
{
    private const string Client = "a-partner-client-for-tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string root = Directory.CreateTempSubdirectory("stubid-store-").FullName;

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    private static readonly string Token = string.Join('.',
        Segment("""{"alg":"RS256","kid":"signing-key-for-tests","typ":"at+jwt"}"""),
        Segment("""{"iss":"https://api.example.invalid/auth/open","message":{"Data":{"client_id":[""" + $"\"{Client}\"" + "]}}}"),
        Segment("not a signature anybody computed"));

    private static readonly string LoginPage =
        "https://a.example.invalid/auth/open/Authentication/Login?ReturnUrl="
        + Uri.EscapeDataString("/auth/open/connect/authorize/callback?authzId=" + Token);

    private static readonly CaptureCase Case = new()
    {
        Id = "CAP-TEST",
        Description = "A redirect carrying a token",
        Settles = "Nothing; this one is a test.",
        Url = "https://a.example.invalid/auth/open/connect/authorize",
        Expected = Disposition.LoginRedirect,
    };

    private static RecordedExchange Answer(string header, string value) => new(
        "GET", Case.Url, [], null, 302, "Found", [new KeyValuePair<string, string>(header, value)], []);

    private void Write(RecordedExchange exchange) => ProcessEnvironment.With(
        () => new FixtureStore(root).WriteAsync(Case, exchange, Ct).GetAwaiter().GetResult(),
        ("STUBID_SIGNICAT_PARTNER_CLIENT_ID", Client));

    private string Written(string file) => File.ReadAllText(Path.Combine(root, Case.Id, file));

    private string[] Halves() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(root, Case.Id), "*.header.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, Case.Id), "*.payload.json")),
    ];

    [Fact]
    public void A_token_in_a_redirect_is_replaced_and_its_halves_written_beside_it()
    {
        Assert.True(SensitiveContent.FindSignedToken(LoginPage).Found, "the guard does not see the sample");

        Write(Answer("Location", LoginPage));

        var head = Written("response.head");
        Assert.False(SensitiveContent.FindSignedToken(head).Found);
        Assert.Contains("authzId%3D{{AUTHZID}}", head, StringComparison.Ordinal);

        Assert.Contains("\"typ\":\"at+jwt\"", Written("authzId.header.json"), StringComparison.Ordinal);

        // Decoded, the scrub can reach what it could not while the payload was base64url.
        var payload = Written("authzId.payload.json");
        Assert.DoesNotContain(Client, payload, StringComparison.Ordinal);
        Assert.Contains("{{SIGNICAT_PARTNER_CLIENT_ID}}", payload, StringComparison.Ordinal);

        using var meta = JsonDocument.Parse(Written("meta.json"));
        var token = meta.RootElement.GetProperty("tokens").GetProperty("authzId");
        Assert.Equal("RS256", token.GetProperty("Algorithm").GetString());
        Assert.Equal("signing-key-for-tests", token.GetProperty("Kid").GetString());
        Assert.Equal(
            Token.Split('.').Select(part => part.Length),
            token.GetProperty("SegmentLengths").EnumerateArray().Select(length => length.GetInt32()));
    }

    /// <remarks>
    /// The login redirect wrapped in one more URL, the way a later hop on the same journey would
    /// carry it. Its token sits behind <c>%253D</c> rather than <c>%3D</c>.
    /// </remarks>
    [Fact]
    public void A_token_nested_a_level_deeper_is_replaced_too()
    {
        var wrapped = "https://a.example.invalid/next?returnUrl=" + Uri.EscapeDataString(LoginPage);
        Assert.True(SensitiveContent.FindSignedToken(wrapped).Found, "the guard does not see the sample");

        Write(Answer("Location", wrapped));

        var head = Written("response.head");
        Assert.False(SensitiveContent.FindSignedToken(head).Found);
        Assert.Contains("authzId%253D{{AUTHZID}}", head, StringComparison.Ordinal);
        Assert.DoesNotContain(Client, Written("authzId.payload.json"), StringComparison.Ordinal);
    }

    /// <remarks>
    /// Every dotted run in a header is looked at now, and most are not tokens. One whose first
    /// segment decodes to JSON that is not an object used to throw halfway through writing a case.
    /// </remarks>
    [Fact]
    public void A_dotted_value_that_is_not_a_token_is_written_as_it_was()
    {
        var value = $"v={Segment("12345678")}.abcdefghij.x; w={Segment("""{"alg":1}""")}.abcdefghij.x";

        Write(Answer("X-Something", value));

        Assert.Contains(value, Written("response.head"), StringComparison.Ordinal);
        Assert.Empty(Halves());
    }

    /// <remarks>
    /// Every recording in the first broker's pack is one of these, and its meta has to come out
    /// the way it always did.
    /// </remarks>
    [Fact]
    public void A_recording_without_a_token_writes_no_token_member_and_no_halves()
    {
        Write(Answer("Location", "https://a.example.invalid/auth/open/Error?errorId=CfDJ8abc"));

        using var meta = JsonDocument.Parse(Written("meta.json"));
        Assert.False(meta.RootElement.TryGetProperty("tokens", out _));
        Assert.Empty(Halves());
    }

    [Fact]
    public void Recording_a_case_again_leaves_no_halves_from_the_last_time()
    {
        Write(Answer("Location", LoginPage));
        Write(Answer("Location", "https://a.example.invalid/auth/open/Error?errorId=CfDJ8abc"));

        Assert.Empty(Halves());
    }

    /// <remarks>
    /// A cookie's value is a credential and is masked whole. Decoding one to write its halves
    /// would publish what the mask exists to hide.
    /// </remarks>
    [Fact]
    public void A_token_in_a_cookie_is_masked_rather_than_decoded()
    {
        Write(Answer("Set-Cookie", $"session={Token}; path=/; secure"));

        Assert.False(SensitiveContent.FindSignedToken(Written("response.head")).Found);
        Assert.Empty(Halves());
    }
}
