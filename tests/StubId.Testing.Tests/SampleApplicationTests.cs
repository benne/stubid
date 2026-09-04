using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StubId.Client;
using StubId.Wire;

namespace StubId.Testing.Tests;

/// <summary>
/// The sample application in <c>samples/aspnetcore</c>, run against a container.
/// </summary>
/// <remarks>
/// The sample is the first thing in this repository a reader can start rather than read, and the
/// guide tells them two commands and expects a login. What makes that claim keepable is that the
/// application hosted here is the sample itself - its own <c>Program.cs</c>, its own configuration,
/// its own certificate pinning - rather than a copy of it that agrees until one of them changes.
/// <para>
/// Only the relying party is in memory. Everything it dials is a real socket to the container's
/// mapped TLS port, so the pinning callback the sample writes is doing real work here, and
/// <see cref="The_sample_refuses_a_certificate_it_did_not_pin" /> is what stops that being a claim
/// nobody checked.
/// </para>
/// </remarks>
[Trait("Category", "Container")]
public class SampleApplicationTests : IAsyncLifetime
{
    private StubIdContainer _stub = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _stub = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct)).WithTls().Build();

        await _stub.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _stub.DisposeAsync();

    /// <summary>The two commands in the guide, minus the person clicking.</summary>
    [Fact]
    public async Task The_sample_signs_a_citizen_in()
    {
        await using var sample = Sample(_stub.Authority.ToString());

        // Read back rather than restated here. The client id is the sample's to choose, and a copy
        // in this file would let the two disagree while both looked right.
        var clientId = sample.Services.GetRequiredService<IConfiguration>()["StubId:ClientId"];

        Assert.False(string.IsNullOrEmpty(clientId), "The sample does not configure a client id.");

        var citizen = await _stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await _stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(clientId!), Ct);

        // https, so the handler builds an https redirect_uri and marks its correlation cookies
        // Secure - the shape a reader's browser will meet. TestServer does not perform a handshake,
        // which is why the guide's own run is still verified by hand.
        var rp = sample.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });

        rp.DefaultRequestVersion = HttpVersion.Version11;

        using var trusting = _stub.CreateTrustingHandler();
        trusting.AllowAutoRedirect = false;
        using var browser = new HttpClient(trusting, disposeHandler: false);

        var cookies = new CookieJar();

        // Reaching a redirect at all means the sample fetched discovery over TLS, through its own
        // pinned handler, with RequireHttpsMetadata untouched.
        using var challenge = await Browser.Send(rp, HttpMethod.Get, "/secure", cookies);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var authorize = challenge.Headers.Location!;

        Assert.Equal(Uri.UriSchemeHttps, authorize.Scheme);
        Assert.StartsWith(_stub.Authority.ToString(), authorize.ToString(), StringComparison.Ordinal);

        using var authorized = await browser.GetAsync(authorize, Ct);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var fields = Browser.HiddenFields(await authorized.Content.ReadAsStringAsync(Ct));

        Assert.True(fields.ContainsKey("code"), "The stub did not post a code back.");

        using var callback = await Browser.Send(
            rp, HttpMethod.Post, "/signin-oidc", cookies, new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/secure", callback.Headers.Location!.ToString());

        using var secure = await Browser.Send(rp, HttpMethod.Get, "/secure", cookies);

        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);

        // The page the guide promises: the claims that came back, not just a 200.
        // The name is the proof that userinfo was reached too, over the same pinned handler: the
        // id_token this broker issues is strict and carries no identity.
        Assert.Contains(
            citizen.Name,
            await secure.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What stops the login above from passing against a sample that trusts anything.
    /// </summary>
    /// <remarks>
    /// A green sign-in is equally consistent with the sample having written a callback that returns
    /// true, which is the habit the whole design exists to avoid, and no assertion about
    /// RequireHttpsMetadata would notice: that check is on the scheme of the metadata address, and
    /// StubId.InProcess satisfies it with no TLS anywhere.
    /// <para>
    /// So the sample is left pinning the container's certificate and then pointed at somebody
    /// else's. The refusal happens in the handshake, which is why the decoy serves nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_sample_refuses_a_certificate_it_did_not_pin()
    {
        using var foreign = CertificateFactory.CreateServerCertificate(
            "Not this instance",
            ["localhost", "127.0.0.1"],
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));

        var decoy = WebApplication.CreateSlimBuilder();
        decoy.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listener => listener.UseHttps(foreign)));

        await using var elsewhere = decoy.Build();
        await elsewhere.StartAsync(Ct);

        var address = elsewhere.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        // The control port is still the real one, so the sample fetches and pins the container's
        // certificate exactly as it does in the test above. Only the authority moves.
        await using var sample = Sample($"{address}/op");

        var rp = sample.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => Browser.Send(rp, HttpMethod.Get, "/secure", new CookieJar()));

        Assert.True(
            Chain(refused).Any(e => e is AuthenticationException),
            "Expected the handshake to be refused, but the failure was: " + refused);

        await elsewhere.StopAsync(Ct);
    }

    /// <summary>What the reader sees when the login is refused instead.</summary>
    /// <remarks>
    /// The guide invites this: start the container with automatic approval off and somebody has to
    /// decide the login, which means somebody can abort it. The refusal here is queued rather than
    /// clicked, because the two are the same bytes - the login page's Abort button and a queued
    /// decision meet at the broker's one refusal path - and what is under test is the sample's
    /// answer to it, not StubID's decision ladder, which has its own tests.
    /// <para>
    /// The sample answered with an empty 400 until somebody aborted a login in a browser and could
    /// not tell it from a crash. A refusal is an outcome, so it gets a page and the broker's own
    /// error code, which is the part <c>docs/guides/approvals.md</c> is written around.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_sample_shows_a_refused_login()
    {
        await using var sample = Sample(_stub.Authority.ToString());

        var clientId = sample.Services.GetRequiredService<IConfiguration>()["StubId:ClientId"];

        Assert.False(string.IsNullOrEmpty(clientId), "The sample does not configure a client id.");

        await _stub.Behaviour.EnqueueAsync(Decision.Refused().ForClient(clientId!), Ct);

        var rp = sample.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });

        rp.DefaultRequestVersion = HttpVersion.Version11;

        using var trusting = _stub.CreateTrustingHandler();
        trusting.AllowAutoRedirect = false;
        using var browser = new HttpClient(trusting, disposeHandler: false);

        var cookies = new CookieJar();

        using var challenge = await Browser.Send(rp, HttpMethod.Get, "/secure", cookies);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        using var authorized = await browser.GetAsync(challenge.Headers.Location!, Ct);
        var fields = Browser.HiddenFields(await authorized.Content.ReadAsStringAsync(Ct));

        Assert.Equal("access_denied", fields.GetValueOrDefault("error"));
        Assert.False(fields.ContainsKey("code"), "A refused login handed out a code.");

        using var callback = await Browser.Send(
            rp, HttpMethod.Post, "/signin-oidc", cookies, new FormUrlEncodedContent(fields));

        var page = await callback.Content.ReadAsStringAsync(Ct);

        // A page rather than a bare status, carrying what the broker actually said.
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("mitid_user_aborted", page, StringComparison.Ordinal);

        // And nobody is signed in, which a page saying so could otherwise hide.
        using var secure = await Browser.Send(rp, HttpMethod.Get, "/secure", cookies);

        Assert.Equal(HttpStatusCode.Redirect, secure.StatusCode);
    }

    /// <summary>
    /// The sample, told where this container answers. Built here rather than in
    /// <see cref="InitializeAsync" /> because it reads the certificate while it composes, so the
    /// container has to be running first.
    /// </summary>
    private WebApplicationFactory<Program> Sample(string authority) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            // Development would install the developer exception page, which turns the refusal
            // the second test is looking for into a tidy 500 and hides what it is about.
            .UseEnvironment("Production")
            .UseSetting("StubId:Authority", authority)
            .UseSetting("StubId:ControlUrl", _stub.MappedAddress.ToString()));

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
