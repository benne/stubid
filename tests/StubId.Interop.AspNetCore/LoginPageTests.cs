extern alias harness;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Signer = harness::StubId.CaptureHarness.RequestObject;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What a parked login shows the person looking at it.
/// </summary>
/// <remarks>
/// <para>
/// The broker builds its authorize page out of the request, and on a signing request the
/// transaction text stands beside the widget. StubID does not wear MitID's furniture, but the
/// text is the request's rather than the broker's, and a person asked to approve something is
/// entitled to see what — so this page carries it and nothing else the request sent.
/// </para>
/// <para>
/// Every case here drives the text in by a different route, because the routes disagree about
/// what the raw query holds: a push leaves the client id and a reference behind it, and a signed
/// request leaves a JWS. A page fed from the query works on the first route and goes blank on
/// the other two.
/// </para>
/// </remarks>
public class LoginPageTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";
    private const string Authority = "http://localhost/op";

    // Excluded by the credential guard's own negative lookahead, and read by nothing: StubID
    // does not check who signed a request object.
    private const string Password = "not-a-real-secret";

    private static readonly DateTimeOffset Issued = new(2026, 9, 2, 9, 45, 0, TimeSpan.Zero);

    /// <summary>"StubID transaction text one", which is what CAP-031 sent.</summary>
    private const string RecordedText = "U3R1YklEIHRyYW5zYWN0aW9uIHRleHQgb25l";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instance that parks a login rather than deciding it, so a page exists at all.</summary>
    private static WebApplicationFactory<Program> Parking() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
        });

    private static string IdpParams(string text, string type = "text") =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["mitid"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["transaction_text"] = text,
                ["transaction_text_type"] = type,
            },
        });

    /// <summary>Follows a parked authorize to the page it parked at.</summary>
    private static async Task<string> Page(HttpClient client, HttpResponseMessage authorize)
    {
        var location = authorize.Headers.Location!.ToString();
        Assert.Contains("/op/Login?session=", location, StringComparison.Ordinal);

        return await client.GetStringAsync(location, Ct);
    }

    private static async Task<string> PageForQuery(HttpClient client, string? idpParams)
    {
        var extra = idpParams is null
            ? ""
            : $"&idp_values=mitid&idp_params={Uri.EscapeDataString(idpParams)}";

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=openid%20mitid%20transaction_token&state=s&nonce=n" + extra, Ct);

        return await Page(client, authorize);
    }

    [Fact]
    public async Task The_page_shows_the_decoded_text_rather_than_the_base64_that_carried_it()
    {
        // The broker's page showed "StubID transaction text one". Rendering the base64 verbatim
        // would leave the same person unable to read what they are approving while letting the
        // gap look closed.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await PageForQuery(client, IdpParams(RecordedText));

        Assert.Contains("StubID transaction text one", page, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedText, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_that_carries_markup_reaches_the_page_as_text()
    {
        // The first client-controlled string this page has ever rendered, in a window a browser
        // was just redirected to from a real authorize request. Escaping the base64 instead of
        // the decoded text is a no-op that looks like a guard, so this asserts on both.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // <script>alert(1)</script>
        var page = await PageForQuery(
            client, IdpParams("PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==", "html"));

        Assert.DoesNotContain("<script>alert(1)</script>", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_that_cannot_be_decoded_says_so_instead_of_vanishing()
    {
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await PageForQuery(client, IdpParams("not base64 at all!"));

        Assert.Contains("could not be decoded", page, StringComparison.Ordinal);
        Assert.Contains("Transaction text", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_carrying_no_text_gets_no_panel()
    {
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.DoesNotContain("Transaction text", await PageForQuery(client, null),
            StringComparison.Ordinal);

        // An idp_params with a mitid section and something else in it, so the panel is
        // conditional on the text rather than on the parameter being there.
        Assert.DoesNotContain(
            "Transaction text",
            await PageForQuery(client, """{"mitid":{"reference_text":"U3R1YklE"}}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pushed_request_reaches_the_page_where_the_raw_query_cannot()
    {
        // The case that decides where the carrier goes. After a push the browser arrives with a
        // client id and a request_uri, so a page reading the query it parked with shows nothing
        // at all — and the failure is a blank panel rather than an error.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var pushed = await client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("scope", "openid mitid transaction_token"),
            new KeyValuePair<string, string>("idp_values", "mitid"),
            new KeyValuePair<string, string>("idp_params", IdpParams(RecordedText)),
        ]), Ct);

        using var reference = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));
        var requestUri = reference.RootElement.GetProperty("request_uri").GetString()!;

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}" +
            $"&request_uri={Uri.EscapeDataString(requestUri)}", Ct);

        Assert.Contains("StubID transaction text one", await Page(client, authorize),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_request_reaches_the_page_too()
    {
        // The route CAP-031 used. The parameters are inside a JWS, so this is the second of the
        // three arrival shapes a query-reading page would lose.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = Signer.Build(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = CodeClient,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "openid mitid transaction_token",
                ["state"] = "CAP-031",
                ["nonce"] = "n",
                ["idp_values"] = "mitid",
                ["idp_params"] = IdpParams(RecordedText),
            },
            CodeClient, Authority, Password, Issued);

        using var authorize = await client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&request={Uri.EscapeDataString(request)}", Ct);

        Assert.Contains("StubID transaction text one", await Page(client, authorize),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_still_names_neither_the_client_nor_anything_else_the_request_carried()
    {
        // The rest of that divergence is unchanged, and this is what keeps the document honest:
        // the text is on the page and the relying party's name is not, because StubID registers
        // no display name for a client at all.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await PageForQuery(client, IdpParams(RecordedText));

        Assert.DoesNotContain(CodeClient, page, StringComparison.Ordinal);
        Assert.Contains("This is StubID, an emulator.", page, StringComparison.Ordinal);
    }
}
