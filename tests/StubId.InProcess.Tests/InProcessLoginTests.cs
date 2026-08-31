using System.Diagnostics;
using System.Net;
using System.Text.Json;
using StubId.Client;

namespace StubId.InProcess.Tests;

/// <summary>
/// A test creates a citizen and drives a whole login, in this process, with no Docker.
/// </summary>
/// <remarks>
/// The milestone's acceptance criterion and the twin of
/// <c>ContainerLoginTests.A_citizen_created_from_a_test_signs_in_through_the_container</c>. The
/// containerised one proves the image; this proves the module a .NET suite reaches for when it
/// would rather not have a Docker daemon in CI at all.
/// <para>
/// Each test builds its own instance, which a containerised suite cannot afford and this one can.
/// It is also what lets the guide quote the opening of this test verbatim rather than an
/// idealisation of it.
/// </para>
/// </remarks>
public class InProcessLoginTests(ITestOutputHelper output)
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_citizen_created_from_a_test_signs_in_through_the_host()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        // Started before the stopwatch, for the reason the container twin starts its own after
        // the container is up: the first instance on a machine generates its signing keys, which
        // is a property of the machine rather than of this codebase. StartupCostTests measures
        // that half against a warm key directory, where the number means something.
        var elapsed = Stopwatch.StartNew();

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

        using var browser = stub.CreateClient();

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
        Assert.Equal(stub.Authority.ToString(), returned["iss"]);

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
            stub.Authority.ToString(),
            Payload(body.RootElement.GetProperty("id_token").GetString()!)
                .RootElement.GetProperty("iss").GetString());

        elapsed.Stop();
        output.WriteLine($"Citizen to token in {elapsed.Elapsed.TotalMilliseconds:0} ms.");

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"A login took {elapsed.Elapsed.TotalSeconds:0.00} s in process.");
    }

    /// <remarks>
    /// The container twin proves the clock reaches through Docker. This proves the builder's
    /// switch reaches configuration, which is a claim about the module rather than about the
    /// emulator, and would be true of an instance the container test never starts.
    /// </remarks>
    [Fact]
    public async Task A_five_minute_timeout_is_reached_without_waiting_five_minutes()
    {
        await using var stub = new StubIdHostBuilder()
            .WithControllableClock()
            .WithAutomaticApproval(false)
            .Build();

        await stub.StartAsync(Ct);

        using var browser = stub.CreateClient();

        // Nothing queued, so the login parks and waits for somebody who never arrives.
        using var authorize = await browser.GetAsync(
            "/op/connect/authorize"
            + $"?client_id={CodeClient}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var parked = await stub.Sessions.ListAsync(SessionState.AwaitingApproval, ct: Ct);

        Assert.NotEmpty(parked);

        await stub.Time.AdvanceAsync(TimeSpan.FromSeconds(301), Ct);

        Assert.Equal(
            SessionState.Expired,
            (await stub.Sessions.FindAsync(parked[0].Id, Ct))?.State);
    }

    private static JsonDocument Payload(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/')
            .PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');

        return JsonDocument.Parse(Convert.FromBase64String(padded));
    }
}
