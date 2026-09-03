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

    /// <summary>What a login is allowed to cost once the instance is up.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    /// <summary>How many logins this will pay for before giving up on finding one under budget.</summary>
    private const int Attempts = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_citizen_created_from_a_test_signs_in_through_the_host()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

        await SignIn(stub);
    }

    /// <summary>What a login costs once the instance is up, judged by the fastest of a few.</summary>
    /// <remarks>
    /// <para>
    /// Separate from the test above, which is the milestone's acceptance criterion and whose
    /// opening the in-process guide quotes verbatim. A cost claim measured inside it would put a
    /// wall clock in a test that is not about time, and a loop through the part the guide quotes.
    /// <c>StartupCostTests</c> is split from it for the same reason and measures the other half.
    /// </para>
    /// <para>
    /// The instance is started off the clock, because the first instance on a machine generates
    /// its signing keys and that cost belongs to the machine rather than to this codebase.
    /// </para>
    /// <para>
    /// The fastest of up to five logins is judged rather than one, for the reason
    /// <c>StartupCostTests</c> gives at length: the noise is strictly additive, so the floor is
    /// the closest thing to what a login actually costs, and a change that makes one more
    /// expensive raises the floor with everything else. This test has not failed the way the
    /// startup one did twice, but it is the same wall clock on the same shared runners, and the
    /// first sample pays for JIT that every later one finds already done.
    /// </para>
    /// <para>
    /// That last part is not a small correction here. Forced to take all five on an idle laptop,
    /// this measured 237, 2, 1, 2 and 1 ms - the first login costing more than a hundred times
    /// the ones behind it. So the single sample the old shape took was spending most of a
    /// four-fold margin on warm-up before the login itself was measured at all, which is a
    /// thinner margin than its clean history suggested.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_login_costs_under_a_second_in_process()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        List<TimeSpan> samples = [];

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var elapsed = Stopwatch.StartNew();

            var citizen = await stub.Citizens.CreateAsync(
                new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
                Ct);

            await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

            await SignIn(stub);

            elapsed.Stop();
            samples.Add(elapsed.Elapsed);

            if (elapsed.Elapsed < Budget)
            {
                break;
            }
        }

        // Every sample, not just the verdict. A cost test that reports only that it failed
        // leaves nobody anything to work with.
        output.WriteLine(
            "Citizen to token in "
            + string.Join(", ", samples.Select(s => $"{s.TotalMilliseconds:0} ms"))
            + $" over {samples.Count} login(s).");

        Assert.True(
            samples.Min() < Budget,
            $"The fastest of {samples.Count} logins took {samples.Min().TotalSeconds:0.00} s in "
            + "process, which is over budget. Every login: "
            + string.Join(", ", samples.Select(s => $"{s.TotalSeconds:0.00} s")));
    }

    /// <summary>
    /// Drives the login the guide's snippet stops short of, and checks what came back.
    /// </summary>
    /// <remarks>
    /// Shared so the acceptance test and the cost test cannot drift into measuring different
    /// logins. The four lines above it stay where they are, because that is what the guide
    /// quotes and a copied example that has quietly stopped working is worse than no example.
    /// </remarks>
    private static async Task SignIn(StubIdHost stub)
    {
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
