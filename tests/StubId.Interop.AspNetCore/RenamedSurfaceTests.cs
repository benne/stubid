using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Client;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The names 2026.09.1 shipped still work, one release after they were replaced.
/// </summary>
/// <remarks>
/// The repository was converted to US English on 2026-09-06, which renamed seven public members
/// and one route that had already shipped. Each kept its old spelling as an alias so a suite
/// written against the published package keeps compiling and keeps answering; this is the test
/// that says the aliases actually forward rather than merely existing.
/// <para>
/// <b>Delete this file when the aliases go.</b> It is the only thing in the tree that uses them -
/// deliberately, because <c>TreatWarningsAsErrors</c> plus CS0618 means anything else that did
/// would fail the build. That is why every one of these is under a pragma.
/// </para>
/// </remarks>
public class RenamedSurfaceTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host() =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
        });

    /// <summary>
    /// The three <c>.Behaviour</c> properties reach the same API object as <c>.Behavior</c>.
    /// </summary>
    /// <remarks>
    /// Reference equality rather than a second enqueue: what the alias promises is that it is the
    /// same object, and two calls that both worked would not tell one object from two.
    /// </remarks>
    [Fact]
    public void The_old_Behaviour_spelling_reaches_the_same_api()
    {
        using var stub = new StubIdClient(Host().CreateClient());

#pragma warning disable CS0618 // The aliases are the subject.
        Assert.Same(stub.Behavior, stub.Behaviour);
#pragma warning restore CS0618
    }

    /// <summary>The alias does not merely resolve; a decision queued through it is taken.</summary>
    [Fact]
    public async Task A_decision_queued_through_the_old_spelling_decides_a_login()
    {
        using var host = Host();
        using var stub = new StubIdClient(host.CreateClient());

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Ida Molbech", DateOfBirth = new DateOnly(1990, 6, 14) }, Ct);

#pragma warning disable CS0618 // The alias is the subject.
        await stub.Behaviour.EnqueueAsync(
            Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);
#pragma warning restore CS0618

        using var browser = host.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var authorize = await browser.GetAsync(Authorize(), Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("code=", authorize.Headers.Location!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route this shipped under still enqueues, and the renamed one does the same thing.
    /// </summary>
    /// <remarks>
    /// Both are driven here rather than only the old one, because an alias that answered while
    /// the new path had been mistyped would pass a test that only exercised the alias.
    /// </remarks>
    [Theory]
    [InlineData("/_stubid/v1/behaviours/enqueue")]
    [InlineData("/_stubid/v1/behaviors/enqueue")]
    public async Task Both_spellings_of_the_enqueue_route_answer(string path)
    {
        using var host = Host();
        using var http = host.CreateClient();
        using var stub = new StubIdClient(host.CreateClient());

        var citizen = await stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Palle Wichmann", DateOfBirth = new DateOnly(1972, 1, 9) }, Ct);

        using var queued = await http.PostAsJsonAsync(
            path,
            new { approve = true, clientId = CodeClient, citizenId = citizen.Id },
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);

        using var browser = host.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var authorize = await browser.GetAsync(Authorize(), Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("code=", authorize.Headers.Location!.Query, StringComparison.Ordinal);
    }

    /// <summary>The renamed server members forward to the ones that replaced them.</summary>
    [Fact]
    public void The_old_server_spellings_forward()
    {
        var state = new BrokerState();

#pragma warning disable CS0618 // The aliases are the subject.
        Assert.Equal(state.OrganizationOf(CodeClient), state.OrganisationOf(CodeClient));

        var client = state.Clients[CodeClient];
        Assert.Equal(client.Organization, client.Organisation);

        Assert.True(PublicBaseUrl.TryNormalise("http://localhost:8080", out var old, out _));
#pragma warning restore CS0618

        Assert.True(PublicBaseUrl.TryNormalize("http://localhost:8080", out var current, out _));
        Assert.Equal(current, old);
    }

    private static string Authorize() =>
        "/op/connect/authorize"
        + $"?client_id={CodeClient}&response_type=code"
        + "&redirect_uri=http://localhost:5099/callback&scope=openid&state=s&nonce=n";
}
