extern alias harness;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Server;
using StubId.Wire;
using Signer = harness::StubId.CaptureHarness.RequestObject;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The <c>request</c> parameter, driven the way the measurement drove it.
/// </summary>
/// <remarks>
/// <para>
/// The objects here are built by the same writer that produced CAP-031's, so a case is a
/// request the broker has actually been sent rather than one assembled to suit the reader.
/// </para>
/// <para>
/// Every case that matters is red without the reader for a reason worth stating: a query
/// carrying only <c>client_id</c>, <c>response_type</c> and <c>request</c> is not a valid
/// authorization request on its own, so before the object is opened it fails on the redirect
/// URI it never sent.
/// </para>
/// </remarks>
public class SignedRequestTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";
    private const string Authority = "http://localhost/op";

    // Excluded by the credential guard's own negative lookahead, and useless besides: nothing
    // here verifies a signature, which is the divergence two of these cases exist to pin.
    private const string Password = "not-a-real-secret";

    /// <summary>Fixed, so an object is the same bytes on every run.</summary>
    private static readonly DateTimeOffset Issued = new(2026, 9, 2, 9, 45, 0, TimeSpan.Zero);

    private readonly HttpClient _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SignedRequestTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>An instance that parks a login instead of approving it, for the two that need one.</summary>
    private static WebApplicationFactory<Program> Parking() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
        });

    private static string Signed(Dictionary<string, string> parameters) =>
        Signer.Build(parameters, CodeClient, Authority, Password, Issued);

    /// <summary>
    /// A compact JWS with a payload chosen by the caller, for the objects the writer will not
    /// produce because they are the ones it exists to get right.
    /// </summary>
    private static string Compact(string payload) =>
        $"{Base64Url.Encode("""{"alg":"HS256","typ":"JWT"}""")}." +
        $"{Base64Url.Encode(payload)}.{Base64Url.Encode("not a signature")}";

    private static Dictionary<string, string> Everything(string scope = "openid mitid") =>
        new(StringComparer.Ordinal)
        {
            ["client_id"] = CodeClient,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = scope,
            ["state"] = "s",
            ["nonce"] = "n",
        };

    /// <summary>The authorize call CAP-031 made: the object, and the two names beside it.</summary>
    private async Task<HttpResponseMessage> Authorize(string request, HttpClient? client = null) =>
        await (client ?? _client).GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&request={Uri.EscapeDataString(request)}", Ct);

    private async Task<JsonElement> Redeem(string code)
    {
        using var token = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
        ]), Ct);

        return JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static string Code(HttpResponseMessage response) =>
        System.Web.HttpUtility
            .ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query)["code"]!;

    private static JsonElement Payload(string compact) =>
        JsonDocument.Parse(Base64Url.Decode(compact.Split('.')[1])).RootElement.Clone();

    [Fact]
    public async Task A_query_carrying_nothing_but_the_object_signs_in()
    {
        // Case E of the measurement, which is what proves the broker takes the parameters out
        // of the object rather than answering the same way it would have without one.
        using var response = await Authorize(Signed(Everything()));

        var location = response.Headers.Location!.ToString();

        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("code=", location, StringComparison.Ordinal);
        Assert.Contains("state=s", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_object_wins_where_the_query_says_something_else()
    {
        // OpenID Connect Core 6.1, and unobservable from CAP-031: its query and its object
        // agree on both names they share. The scope decides whether a transaction token is
        // issued at all, so the two answers are far apart.
        var request = Signed(Everything("openid mitid transaction_token"));

        using var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code&scope=openid" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&request={Uri.EscapeDataString(request)}", Ct);

        var body = await Redeem(Code(response));

        Assert.Equal("openid mitid transaction_token", body.GetProperty("scope").GetString());
        Assert.True(body.TryGetProperty("transaction_token", out _));
    }

    [Fact]
    public async Task The_broker_s_own_parameters_come_out_of_the_object_too()
    {
        // What the object is for. CAP-031 carried idp_params inside it, and a reader that
        // merged only the OAuth names would take this request as far as a login and lose the
        // one thing it was signed to carry.
        var parameters = Everything("openid mitid transaction_token");
        parameters["idp_values"] = "mitid";
        parameters["idp_params"] = """{"mitid":{"reference_text":"U3R1YklEIHJlZmVyZW5jZSB0ZXh0"}}""";

        using var response = await Authorize(Signed(parameters));
        var body = await Redeem(Code(response));

        Assert.Equal(
            "U3R1YklEIHJlZmVyZW5jZSB0ZXh0",
            Payload(body.GetProperty("transaction_token").GetString()!)
                .GetProperty("mitid.reference_text").GetString());
    }

    [Fact]
    public async Task A_parameter_the_ladder_reads_arrives_from_inside_the_object()
    {
        // Parse is not the only reader. The parked session gets its own copy of the parameters,
        // and the simulation parameter is decided from that copy rather than from the request -
        // so an object merged into one and not the other signs in and ignores what it asked for.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var parameters = Everything();
        parameters["simulation"] = "no-ui";

        using var response = await Authorize(Signed(parameters), client);
        var location = response.Headers.Location!.ToString();

        // Straight back to the client with a code, with no page rendered in between.
        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("code=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interaction_the_object_forbids_is_refused_to_the_object_s_redirect_uri()
    {
        // The measurement's own way back: the same signed object with prompt=none and no session
        // behind it, refused by redirecting to the client. Both names on that redirect came out
        // of the object, which is the half of the merge the success path cannot show.
        await using var factory = Parking();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var parameters = Everything();
        parameters["state"] = "CAP-031";
        parameters["prompt"] = "none";

        using var response = await Authorize(Signed(parameters), client);
        var location = response.Headers.Location!.ToString();

        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("error=login_required", location, StringComparison.Ordinal);
        Assert.Contains("state=CAP-031", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pushed_object_is_read_where_it_is_pushed()
    {
        // The push is the endpoint the measurement used, and it is a separate reader: it builds
        // its own parameters and never reaches the authorize path at all.
        using var pushed = await _client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("request", Signed(Everything())),
        ]), Ct);

        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);

        using var reference = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync(Ct));
        var requestUri = reference.RootElement.GetProperty("request_uri").GetString();

        using var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}" +
            $"&request_uri={Uri.EscapeDataString(requestUri!)}", Ct);

        Assert.StartsWith(RedirectUri, response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Objects the broker will not read, and the ways a reader gets each of them wrong.
    /// </summary>
    /// <remarks>
    /// <c>exp</c> is the expensive one. Its absence earns bytes identical to a forged signature,
    /// so a probe that omits it fails every case it tries including its own negative control and
    /// reads as a clean "signed requests do not work here" - which is what the first pass at the
    /// measurement concluded, on two clients, before the missing claim was found.
    /// </remarks>
    public static TheoryData<string> ObjectsThatCannotBeRead() =>
    [
        "not-a-jwt",
        "two.segments",
        "four.segments.in.here",
        $"{Base64Url.Encode("""{"alg":"HS256"}""")}.@@@@.{Base64Url.Encode("x")}",
        Compact("not json"),
        Compact("null"),
        Compact("123"),
        Compact("[1,2]"),
        Compact("""{"scope":"openid mitid"}"""),
        Compact("""{"exp":1788343339,"scope":"\ud800"}"""),
    ];

    [Theory]
    [MemberData(nameof(ObjectsThatCannotBeRead))]
    public async Task An_object_that_cannot_be_read_sends_the_browser_to_the_error_page(string request)
    {
        // The authorize endpoint refuses one the way it refuses everything else it will not
        // process: its own page, an opaque reference, and nothing the client ever sees.
        //
        // The query beside it is complete and would sign in on its own. That is the whole point
        // of the case: an unreadable object has to be refused rather than stepped over, and a
        // query carrying only the object would have failed on its missing redirect URI instead,
        // which is the same answer for a different reason.
        using var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            $"&state=s&nonce=n&request={Uri.EscapeDataString(request)}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/op/Error", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ObjectsThatCannotBeRead))]
    public async Task A_push_says_what_it_thinks_of_an_object_it_cannot_read(string request)
    {
        // The same validation, from the endpoint willing to say what it thinks. Switching to it
        // is what turned the first wrong answer of the measurement around, so the body is worth
        // asserting byte for byte rather than by its code alone.
        using var response = await _client.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", CodeClient),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("request", request),
        ]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            """{"error":"invalid_request_object","error_description":"Invalid JWT request"}""",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_object_nobody_signed_is_read_anyway_and_its_parameters_are_honoured()
    {
        // The divergence, driven. The broker refuses this: a flipped signature byte and a random
        // key were both measured earning invalid_request_object. StubID holds no secret to check
        // one against, so it reads the object instead - and the assertion is that the parameters
        // arrive, not merely that nothing was refused, because "not refused" is also what a
        // reader that ignored the object entirely would produce.
        var segments = Signed(Everything("openid mitid transaction_token")).Split('.');
        var forged = $"{segments[0]}.{segments[1]}.{Base64Url.Encode("nobody signed this")}";

        using var response = await Authorize(forged);
        var body = await Redeem(Code(response));

        Assert.Equal("openid mitid transaction_token", body.GetProperty("scope").GetString());
        Assert.True(body.TryGetProperty("transaction_token", out _));
    }

    [Fact]
    public async Task An_empty_request_parameter_is_no_object_at_all()
    {
        // Unmeasured: no probe sent one. Treated as absent, which is what every other optional
        // parameter here does with an empty value, so a client that always writes the name and
        // sometimes has nothing to put in it is not refused for it.
        using var response = await _client.GetAsync(
            $"/op/connect/authorize?client_id={CodeClient}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid%20mitid" +
            "&state=s&nonce=n&request=", Ct);

        Assert.StartsWith(RedirectUri, response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_JWT_s_own_claims_do_not_become_request_parameters()
    {
        // Called rather than driven, because no endpoint reads any of these six: an object that
        // leaked them into the parameters would sign in exactly the same way, and the only place
        // it would show is a parked session's parameter view.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["request"] = Signed(Everything()),
        };

        Assert.True(RequestObject.TryMerge(parameters));
        Assert.Equal("openid mitid", parameters["scope"]);

        foreach (var registered in new[] { "iss", "aud", "exp", "iat", "nbf", "jti" })
        {
            Assert.DoesNotContain(registered, parameters.Keys);
        }
    }
}
