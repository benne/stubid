using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StubId.Profiles;
using StubId.Profiles.Idura;
using StubId.Server;

namespace StubId.Profiles.Tests;

/// <summary>
/// Whether the seam can express a second broker, which is the only reason it exists.
/// </summary>
/// <remarks>
/// An abstraction designed around one example is usually wrong, so Idura's route table is
/// declared and served here before anything is built on the seam. What it exercises that Nets
/// eID Broker never would: an issuer at the bare host, a dynamic segment before a literal
/// <c>.well-known</c>, that segment applying to only some routes, tolerant path matching, and
/// an endpoint whose status depends on the query string.
/// </remarks>
public class IduraSeamTests
{
    private static readonly IduraClient Known = new("urn:idura:dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Segment(string acr) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(acr));

    private static async Task<IHost> Serve(IBrokerProfile profile)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<ProfileEndpointDataSource>();
                });
                web.Configure(app =>
                {
                    var routes = app.ApplicationServices.GetRequiredService<ProfileEndpointDataSource>();
                    routes.Load([(profile, new ProfileContext(
                        "https://samples.criipto.id", "https://samples.criipto.id"), "")]);

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        ((IEndpointRouteBuilder)endpoints).DataSources.Add(routes));
                });
            })
            .StartAsync(Ct);

        return host;
    }

    [Fact]
    public async Task A_second_broker_declares_its_routes_without_the_interface_changing()
    {
        // The acceptance criterion. IduraProfile lives in its own assembly and implements
        // IBrokerProfile as written for Nets eID Broker.
        using var host = await Serve(new IduraProfile([Known]));
        var client = host.GetTestClient();

        using var response = await client.GetAsync("/.well-known/openid-configuration", Ct);

        // 501, because no Idura login has been recorded and inventing the bytes is the thing
        // this project exists to avoid. The route resolving is the point.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task The_dynamic_segment_resolves_only_for_an_acr_the_tenant_answers_for()
    {
        using var host = await Serve(new IduraProfile([Known]));
        var client = host.GetTestClient();

        using var known = await client.GetAsync(
            $"/{Segment("urn:grn:authn:dk:mitid:substantial")}/.well-known/openid-configuration", Ct);
        using var unknown = await client.GetAsync(
            $"/{Segment("urn:grn:authn:no:bankid")}/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, known.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_dynamic_segment_does_not_apply_to_every_route()
    {
        // Idura 404s the segment in front of the key set and the token endpoint. A stub that
        // served them there would pass a client the real broker refuses.
        using var host = await Serve(new IduraProfile([Known]));
        var client = host.GetTestClient();
        var acr = Segment("urn:grn:authn:dk:mitid:substantial");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/{acr}/.well-known/jwks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync("/.well-known/jwks", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_base64url_segment_is_refused_where_standard_base64_is_expected()
    {
        // Not pedantry: '-' and '_' are not standard-base64 characters, which is what stops a
        // root-mounted tenant's dynamic first segment from ever swallowing /_stubid/...
        using var host = await Serve(new IduraProfile([Known]));
        var client = host.GetTestClient();

        var url = Segment("urn:grn:authn:dk:mitid:substantial").Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using var response = await client.GetAsync($"/{url}/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("", HttpStatusCode.OK)]
    [InlineData("?client_id=urn:idura:dev", HttpStatusCode.OK)]
    [InlineData("?client_id=urn:nobody", HttpStatusCode.NotFound)]
    public async Task The_configuration_probe_answers_by_query_string(string query, HttpStatusCode expected)
    {
        // Routing cannot express a status that depends on the query, so this proves a profile
        // handler can. The SDK looks for its own client id in the response and throws before
        // authorize is ever reached if it is absent.
        using var host = await Serve(new IduraProfile([Known]));
        var client = host.GetTestClient();

        using var response = await client.GetAsync($"/.well-known/criipto-configuration{query}", Ct);

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.NotEqual(0, body.RootElement.GetProperty("clients").GetArrayLength());
        }
    }
}
