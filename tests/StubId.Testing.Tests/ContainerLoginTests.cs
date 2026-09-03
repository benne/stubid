using System.Diagnostics;
using System.Net;
using System.Text.Json;
using StubId.Client;

namespace StubId.Testing.Tests;

/// <summary>
/// A test creates a citizen and drives a whole login against the container.
/// </summary>
/// <remarks>
/// The milestone's acceptance criterion, and the C# twin of the container verification script. That
/// script stays: it checks the published image before .NET is installed in CI, and this checks the
/// module a .NET suite would actually reach for.
/// <para>
/// The outcome is queued rather than approved after the fact. A parked login can be resumed now,
/// so this is a preference rather than the only way: queueing is one request instead of three and
/// needs no page to drive, which is what a suite without a browser wants.
/// </para>
/// </remarks>
[Trait("Category", "Container")]
[Collection(StubIdCollection.Name)]
public class ContainerLoginTests(StubIdInstance stub, ITestOutputHelper output)
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_citizen_created_from_a_test_signs_in_through_the_container()
    {
        await stub.Container.ResetAsync(Ct);

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = stub.Container.MappedAddress,
        };

        var elapsed = Stopwatch.StartNew();

        var citizen = await stub.Container.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await stub.Container.Behaviour.EnqueueAsync(
            Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

        using var authorize = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var location = authorize.Headers.Location
            ?? throw new InvalidOperationException($"Authorize answered {authorize.StatusCode} with no redirect.");
        var returned = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = returned["code"];

        Assert.False(string.IsNullOrEmpty(code), $"No code on {location}.");
        Assert.Equal(stub.Container.Authority.ToString(), returned["iss"]);

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

        foreach (var member in new[] { "id_token", "access_token", "token_type", "scope" })
        {
            Assert.True(body.RootElement.TryGetProperty(member, out _), $"No {member} in the token response.");
        }

        Assert.Equal(
            stub.Container.Authority.ToString(),
            Payload(body.RootElement.GetProperty("id_token").GetString()!)
                .RootElement.GetProperty("iss").GetString());

        elapsed.Stop();
        output.WriteLine($"Citizen to token in {elapsed.Elapsed.TotalMilliseconds:0} ms.");

        // Not a stopwatch on Docker: the container is already up. This is the budget for the work a
        // test actually drives, and it exists to catch a regression that makes it pathological.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"A login took {elapsed.Elapsed.TotalSeconds:0.0} s against a running container.");
    }

    /// <remarks>
    /// The broker gives a login about five minutes. Waiting that out in CI is not an option, so the
    /// clock is injected - and this proves the injection reaches through the container, not only
    /// through an in-process host.
    /// </remarks>
    [Fact]
    public async Task A_five_minute_timeout_is_reached_without_waiting_five_minutes()
    {
        await stub.Container.ResetAsync(Ct);

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = stub.Container.MappedAddress,
        };

        // Nothing queued, so the login parks and waits for somebody who never arrives.
        using var authorize = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var parked = await stub.Container.Sessions.ListAsync(SessionState.AwaitingApproval, ct: Ct);

        Assert.NotEmpty(parked);

        await stub.Container.Time.AdvanceAsync(TimeSpan.FromSeconds(301), Ct);

        Assert.Equal(
            SessionState.Expired,
            (await stub.Container.Sessions.FindAsync(parked[0].Id, Ct))?.State);
    }

    private static JsonDocument Payload(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/')
            .PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');

        return JsonDocument.Parse(Convert.FromBase64String(padded));
    }
}
