using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StubId.Client;

namespace StubId.Testing.Tests;

/// <summary>
/// A stock ASP.NET Core application signs in against the container over TLS, changing its authority
/// and nothing else.
/// </summary>
/// <remarks>
/// The claim this slice exists to make good. The handler's RequireHttpsMetadata defaults to true and
/// is left alone here, which is only possible because the instance actually serves TLS - against the
/// plain-HTTP container the same configuration fails, and the usual advice is to turn the check off.
/// That advice is the problem: a relaxation added for a test is one copied line from a production
/// client that accepts an unsecured metadata document.
/// <para>
/// The only thing that is not stock is the back channel's certificate trust, and that is the module
/// handing over the certificate this instance generated rather than any weakening of validation.
/// </para>
/// </remarks>
[Trait("Category", "Container")]
public class StockClientOverTlsTests : IAsyncLifetime
{
    private const string ClientId = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private StubIdContainer _stub = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _stub = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct)).WithTls().Build();

        await _stub.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task A_stock_client_signs_in_with_the_https_check_left_on()
    {
        var citizen = await _stub.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        await _stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(ClientId), Ct);

        using var relyingParty = await StartRelyingParty();
        var rp = relyingParty.GetTestClient();
        rp.DefaultRequestVersion = HttpVersion.Version11;

        using var trusting = _stub.CreateTrustingHandler();
        trusting.AllowAutoRedirect = false;
        using var browser = new HttpClient(trusting, disposeHandler: false);

        var cookies = new CookieJar();

        // The application challenges. Reaching this at all means the handler fetched discovery over
        // TLS with its own https requirement intact.
        using var challenge = await Browser.Send(rp, HttpMethod.Get, "/secure", cookies);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var authorize = challenge.Headers.Location!;

        Assert.Equal(Uri.UriSchemeHttps, authorize.Scheme);
        Assert.StartsWith(_stub.Authority.ToString(), authorize.ToString(), StringComparison.Ordinal);

        // The stub decides the login and posts the result back to the application.
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
        Assert.False(string.IsNullOrWhiteSpace(await secure.Content.ReadAsStringAsync(Ct)));
    }

    private async Task<IHost> StartRelyingParty()
    {
        var backchannel = _stub.CreateTrustingHandler();

        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services
                        .AddAuthentication(options =>
                        {
                            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                        })
                        .AddCookie()
                        .AddOpenIdConnect(options =>
                        {
                            // Everything a real integration sets, and nothing more. In particular
                            // RequireHttpsMetadata is not mentioned, so it keeps its default of true.
                            options.Authority = _stub.Authority.ToString();
                            options.ClientId = ClientId;
                            options.ClientSecret = "the-secret-the-existing-configuration-carries";
                            options.ResponseType = "code";
                            options.Scope.Clear();
                            options.Scope.Add("openid");
                            options.Scope.Add("mitid");

                            // Trust for this instance's certificate, which is the one thing a
                            // self-signed transport genuinely requires. Validation itself is intact.
                            options.BackchannelHttpHandler = backchannel;

                            options.Events.OnRemoteFailure = context =>
                            {
                                context.Response.StatusCode = 400;
                                context.HandleResponse();
                                return Task.CompletedTask;
                            };
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/secure", (HttpContext http) =>
                            http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? "no subject").RequireAuthorization());
                });
            })
            .StartAsync(Ct);
    }
}
