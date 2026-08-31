using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StubId.InProcess.Tests;

/// <summary>
/// A stock ASP.NET Core application reaches the module with its metadata check left on.
/// </summary>
/// <remarks>
/// The claim the module exists to make good: two properties are the whole configuration, and
/// neither of them is a relaxation. The handler's RequireHttpsMetadata is not mentioned anywhere
/// below, so it keeps its default of true, and it is satisfied because the authority is https -
/// even though nothing on this machine could dial that name.
/// <para>
/// One challenge rather than a whole sign-in, and that is deliberate.
/// <c>StubId.Interop.AspNetCore/StockClientTests</c> already drives a complete login against the
/// same server, cookies and form post included. Copying a hundred lines of browser emulation here
/// would re-prove that the framework accepts what StubID emits; what is new is only that
/// <c>Authority</c> and <c>CreateHandler()</c> are sufficient wiring. Reaching the redirect at all
/// means the handler fetched the discovery document over the back channel with its https
/// requirement intact and pushed an authorization request, which is everything this test is for.
/// </para>
/// </remarks>
public class StockClientTests
{
    private const string ClientId = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stock_client_reaches_the_module_with_the_https_check_left_on()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        using var relyingParty = await StartRelyingParty(stub);

        var rp = relyingParty.GetTestClient();
        rp.DefaultRequestVersion = HttpVersion.Version11;

        using var challenge = await rp.GetAsync("/secure", Ct);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var authorize = challenge.Headers.Location!;

        Assert.StartsWith(stub.Authority.ToString(), authorize.ToString(), StringComparison.Ordinal);

        // Discovery advertises pushed authorization requests, so a current handler makes one
        // before it redirects. Its reference on the redirect is the proof that the back channel
        // reached the stub, rather than that the handler merely built a URL from a string.
        Assert.Contains("request_uri=", authorize.Query, StringComparison.Ordinal);
    }

    private static async Task<IHost> StartRelyingParty(StubIdHost stub)
    {
        var backchannel = stub.CreateHandler();

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
                            // Everything a real integration sets, and nothing more. The two lines
                            // that name StubID are the two the module exists to provide.
                            options.Authority = stub.Authority.ToString();
                            options.ClientId = ClientId;
                            options.ClientSecret = "the-secret-the-existing-configuration-carries";
                            options.ResponseType = "code";
                            options.Scope.Clear();
                            options.Scope.Add("openid");
                            options.Scope.Add("mitid");

                            options.BackchannelHttpHandler = backchannel;
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/secure", () => "signed in").RequireAuthorization());
                });
            })
            .StartAsync(Ct);
    }
}
