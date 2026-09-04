using System.Net;
using System.Reflection;
using System.Text.Json;
using StubId.Client;
using StubId.Testing;

namespace StubId.Consume.Tests;

/// <summary>
/// The README's example, run the way a reader runs it: from the published package, against the
/// published image, on a machine that has never seen this repository's source.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this repository proves StubID works when built from the working tree.
/// None of it can notice the ways publishing fails on its own - a package that was pushed but
/// never indexed, one unlisted by mistake, a dependency in the graph that did not make it, an
/// image tag that stopped resolving. Those are invisible from inside, and they break exactly
/// the instruction the guides give.
/// </para>
/// <para>
/// This project is deliberately outside StubID.slnx, with its own empty Directory.Build.props
/// and a nuget.config that clears every source but nuget.org. In the solution it would drag a
/// network dependency into every pull request, and with the repository's own settings in scope
/// it would not be testing what it claims to test.
/// </para>
/// </remarks>
public class SignsInTests
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The headline claim of both guides: install the package, run the container, sign in.
    /// </summary>
    [Fact]
    public async Task The_published_package_and_image_sign_a_citizen_in()
    {
        await using var stub = new StubIdBuilder().Build();
        await stub.StartAsync(Ct);

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = stub.MappedAddress,
        };

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await stub.Behaviour.EnqueueAsync(
            Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

        using var authorize = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var location = authorize.Headers.Location
            ?? throw new InvalidOperationException(
                $"Authorize answered {authorize.StatusCode} with no redirect.");

        var code = System.Web.HttpUtility.ParseQueryString(location.Query)["code"];
        Assert.False(string.IsNullOrEmpty(code), $"No code on {location}.");

        using var token = await browser.PostAsync(
            "/op/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = CodeClient,
                ["client_secret"] = "any",
            }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
        Assert.True(body.RootElement.TryGetProperty("id_token", out var idToken));
        Assert.False(string.IsNullOrEmpty(idToken.GetString()));
    }

    /// <summary>
    /// The module's default image is a real tag, resolved by pulling rather than by reading the
    /// constant back.
    /// </summary>
    /// <remarks>
    /// A constant naming a tag nobody published is exactly the state this repository was in
    /// before its first release, and no test inside it could see the problem: the in-repository
    /// suite sets STUBID_TEST_IMAGE to something built locally, precisely so it does not depend
    /// on the registry.
    /// </remarks>
    [Fact]
    public async Task The_image_the_module_defaults_to_can_actually_be_pulled()
    {
        await using var stub = new StubIdBuilder().Build();
        await stub.StartAsync(Ct);

        using var http = new HttpClient { BaseAddress = stub.MappedAddress };
        using var discovery = await http.GetAsync("/op/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);

        using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(Ct));
        Assert.Equal(
            stub.Authority.ToString(),
            document.RootElement.GetProperty("issuer").GetString());
    }

    /// <summary>
    /// The version resolved from nuget.org is the one the release published.
    /// </summary>
    /// <remarks>
    /// Only asserted when the workflow names a version; the scheduled run floats deliberately,
    /// so that it checks what a reader gets today rather than what was true when it was written.
    /// The comparison drops the padding from both sides, because a container tag keeps its
    /// leading zero and a package version cannot.
    /// </remarks>
    [Fact]
    public void The_package_restored_is_the_version_the_workflow_asked_for()
    {
        var expected = Environment.GetEnvironmentVariable("STUBID_EXPECTED_VERSION");

        Assert.SkipWhen(string.IsNullOrEmpty(expected), "No version pinned; this run floats.");

        var informational = typeof(StubIdBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Equal(Unpadded(expected!), Unpadded(informational));
    }

    private static string Unpadded(string version) =>
        string.Join('.', version.Split('.').Select(part =>
            int.TryParse(part, out var n) ? n.ToString() : part));
}
