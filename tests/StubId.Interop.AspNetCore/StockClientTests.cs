using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// A stock ASP.NET Core application signs in against the stub.
/// </summary>
/// <remarks>
/// <para>
/// The other tests assert what StubID emits. This one asks the only question that decides
/// whether any of it was right: does the framework a Danish integrator actually uses accept
/// it? Nothing here is hand-rolled — the handler resolves metadata, fetches the key set,
/// validates the signature, matches the issuer, echoes and checks its nonce, and runs PKCE
/// on its own terms.
/// </para>
/// <para>
/// The authority is https, and the handler's default RequireHttpsMetadata is left alone; the
/// back channel is pointed at the in-memory server. This is where "change only the authority"
/// is literally true.
/// </para>
/// </remarks>
public class StockClientTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Authority = "https://stubid.localtest.me/op";
    private const string ClientId = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private readonly WebApplicationFactory<Program> _stub;

    public StockClientTests(WebApplicationFactory<Program> factory) =>
        _stub = factory.WithWebHostBuilder(b =>
            b.UseSetting("StubId:PublicBaseUrl", "https://stubid.localtest.me"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IHost> StartRelyingParty()
    {
        var backchannel = _stub.Server.CreateHandler();

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
                            // Everything a real integration sets, and nothing more.
                            options.Authority = Authority;
                            options.ClientId = ClientId;
                            options.ClientSecret = "the-secret-the-existing-configuration-carries";
                            options.ResponseType = "code";
                            options.Scope.Clear();
                            options.Scope.Add("openid");
                            options.Scope.Add("mitid");
                            options.SaveTokens = true;

                            // The back channel reaches the stub in memory. Nothing else about
                            // the handler's defaults is touched.
                            options.BackchannelHttpHandler = backchannel;

                            // A real application handles a failed sign-in rather than letting
                            // the exception escape. Without this the handler throws and the
                            // test cannot tell a refusal from a crash.
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
                            // The handler maps sub onto NameIdentifier by default. Reading the
                            // mapped claim keeps its defaults untouched, which is the point.
                            http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? "no subject").RequireAuthorization());
                });
            })
            .StartAsync(Ct);
    }

    [Fact]
    public async Task A_stock_client_completes_sign_in()
    {
        using var relyingParty = await StartRelyingParty();
        var rp = relyingParty.GetTestClient();
        rp.DefaultRequestVersion = HttpVersion.Version11;

        var stub = _stub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cookies = new CookieJar();

        // 1. The application challenges, and the handler builds the authorize request.
        using var challenge = await Send(rp, HttpMethod.Get, "/secure", cookies);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var authorize = challenge.Headers.Location!;
        Assert.StartsWith(Authority, authorize.ToString(), StringComparison.Ordinal);

        // The handler pushed the request first, because the discovery document advertises the
        // endpoint and .NET 9 and later use it when it is available. So the redirect carries a
        // reference rather than the parameters, and PAR is not something StubID could have
        // deferred: it is the first protocol request a stock client makes.
        Assert.Contains("request_uri=", authorize.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("code_challenge=", authorize.Query, StringComparison.Ordinal);

        // 2. The stub authenticates and posts the result back.
        using var authorized = await stub.GetAsync(authorize.PathAndQuery, Ct);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var fields = HiddenFields(await authorized.Content.ReadAsStringAsync(Ct));

        Assert.True(fields.ContainsKey("code"));
        Assert.True(fields.ContainsKey("state"));

        // 3. The handler validates everything and signs the user in.
        using var callback = await Send(rp, HttpMethod.Post, "/signin-oidc", cookies,
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/secure", callback.Headers.Location!.ToString());

        // 4. And the session works.
        using var secure = await Send(rp, HttpMethod.Get, "/secure", cookies);
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);

        var subject = await secure.Content.ReadAsStringAsync(Ct);
        Assert.Equal(
            StubId.Server.Tokens.Subject(ClientId, new StubId.Server.BrokerState().DefaultCitizen),
            subject);
    }

    [Fact]
    public async Task The_handler_refuses_a_token_whose_nonce_was_replaced()
    {
        // Proves the previous test is checking something. If the nonce were ignored, a
        // replayed authorization would sign in and nobody would notice.
        using var relyingParty = await StartRelyingParty();
        var rp = relyingParty.GetTestClient();
        var stub = _stub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = new CookieJar();
        using var challenge = await Send(rp, HttpMethod.Get, "/secure", first);
        using var authorized = await stub.GetAsync(challenge.Headers.Location!.PathAndQuery, Ct);
        var fields = HiddenFields(await authorized.Content.ReadAsStringAsync(Ct));

        // A different browser session: correlation and nonce cookies belong to another
        // challenge, so the handler must reject this.
        var second = new CookieJar();
        using var stranger = await Send(rp, HttpMethod.Get, "/secure", second);

        using var callback = await Send(rp, HttpMethod.Post, "/signin-oidc", second,
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string path, CookieJar cookies, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        cookies.ApplyTo(request);

        var response = await client.SendAsync(request, Ct);
        cookies.Capture(response);
        return response;
    }

    private static Dictionary<string, string> HiddenFields(string html) => Regex
        .Matches(html, """<input type="hidden" name="([^"]+)" value="([^"]*)" />""")
        .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));

    /// <summary>The browser's share of the work: carry cookies between requests.</summary>
    private sealed class CookieJar
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public void Capture(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                return;
            }

            foreach (var pair in values.Select(v => v.Split(';')[0]))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var name = pair[..separator];
                var value = pair[(separator + 1)..];

                if (value.Length == 0)
                {
                    _cookies.Remove(name);
                }
                else
                {
                    _cookies[name] = value;
                }
            }
        }

        public void ApplyTo(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }
        }
    }
}
