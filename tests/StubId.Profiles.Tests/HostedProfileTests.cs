using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.TestHost;
using StubId.Profiles.Idura;
using StubId.Server;

namespace StubId.Profiles.Tests;

/// <summary>
/// Whether the whole application, and not just the route loader, can serve a broker that is not
/// the first one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IduraSeamTests" /> proves the route loader can express a second broker, and until
/// now that was all anything proved. It builds a bare host with routing on it and calls
/// <c>Load</c> directly - no <c>AddStubId</c>, no <c>UseStubId</c>, and in particular no path
/// gate. Loaded into the real pipeline as it stood, every one of Idura's routes would have
/// answered 404 before routing ever saw it, because the gate was constructed with the literal
/// <c>/op</c>.
/// </para>
/// <para>
/// So this is the same profile through the composition an instance actually runs. Idura is not a
/// broker this build serves - <see cref="StubId.Server.BrokerProfiles.Available" /> does not name it and it answers
/// 501 for everything - which is exactly what makes it the right probe: nothing it does here can
/// be true by accident of also being what the first broker does.
/// </para>
/// </remarks>
public class HostedProfileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Address = "http://stubid-hosted.invalid";

    private static string Segment(string acr) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(acr));

    /// <summary>
    /// The real composition, with a profile the build does not name registered over the one it
    /// does.
    /// </summary>
    /// <remarks>
    /// Registered after <c>AddStubId</c> so that the last registration wins, which is how a
    /// profile that is not in the switch gets loaded at all. The gate and the route loader both
    /// resolve <see cref="IBrokerProfile" /> rather than capturing it, so both follow the
    /// override.
    /// </remarks>
    private static async Task<WebApplication> Serve()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["StubId:PublicBaseUrl"] = Address });

        builder.Services.AddStubId(builder.Configuration);
        builder.Services.AddSingleton<IBrokerProfile>(new IduraProfile([new IduraClient("urn:idura:dev")]));

        var app = builder.Build();
        app.UseStubId();

        await app.StartAsync(Ct);

        return app;
    }

    /// <summary>
    /// A profile at the host root is reachable, which is the whole of what this file is for.
    /// </summary>
    [Fact]
    public async Task A_root_mounted_profile_answers_where_it_declared_it_would()
    {
        await using var app = await Serve();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/.well-known/openid-configuration", Ct);

        // 501 rather than 200 because Idura is a spike. What matters is that it is not the 404
        // the path gate used to give every path outside /op.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal("not_implemented", body.RootElement.GetProperty("error").GetString());
        Assert.Contains(
            "profile-seam.md",
            body.RootElement.GetProperty("reason").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// StubID's own surface survives a tenant whose first path segment is dynamic.
    /// </summary>
    /// <remarks>
    /// The gate lets <c>/_stubid</c> through before it consults the profile at all, and the acr
    /// segment is standard base64, whose alphabet excludes the underscore. Either one alone would
    /// do it; both is the point, because the second is what keeps the router from matching
    /// <c>_stubid</c> as a tenant segment once the gate has let it past.
    /// </remarks>
    [Fact]
    public async Task The_control_surface_is_still_reachable_under_a_root_mounted_tenant()
    {
        await using var app = await Serve();
        using var client = app.GetTestClient();

        using var health = await client.GetAsync("/_stubid/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var routes = await client.GetAsync("/_stubid/v1/routes", Ct);

        Assert.Equal(HttpStatusCode.OK, routes.StatusCode);

        using var body = JsonDocument.Parse(await routes.Content.ReadAsStringAsync(Ct));
        var patterns = body.RootElement.GetProperty("routes")
            .EnumerateArray()
            .Select(route => route.GetProperty("pattern").GetString())
            .ToList();

        // The instance reports the routes it actually loaded, so this is also the check that the
        // override reached the data source and not only the gate.
        Assert.Contains("/oauth2/token", patterns);
        Assert.DoesNotContain(patterns, pattern => pattern!.StartsWith("/op/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The gate is the profile's, not the application's: Idura tolerates what Nets eID Broker
    /// refuses.
    /// </summary>
    /// <remarks>
    /// Both brokers are right about themselves. Being stricter than a broker fails a client that
    /// works against it, which is the same class of error as being looser, pointing the other
    /// way - so one application-wide rule is wrong for one of them whichever rule it is.
    /// </remarks>
    [Fact]
    public async Task A_trailing_slash_reaches_a_profile_that_tolerates_one()
    {
        await using var app = await Serve();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/oauth2/userinfo/", Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    /// <summary>The dynamic segment reaches the two routes that carry it, decoded.</summary>
    [Fact]
    public async Task An_acr_scoped_route_is_reachable_through_the_gate()
    {
        await using var app = await Serve();
        using var client = app.GetTestClient();

        var segment = Segment("urn:grn:authn:dk:mitid:substantial");

        using var response = await client.GetAsync(
            $"/{segment}/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Contains(
            "urn:grn:authn:dk:mitid:substantial",
            body.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The issuer follows the profile's root rather than a segment the engine assumed.
    /// </summary>
    /// <remarks>
    /// Idura's issuer is the bare host. Nets eID Broker's ends in <c>/op</c>. The engine used to
    /// compose the second unconditionally, in the handler, from a literal - so a profile declaring
    /// the first would have served documents no client could reconcile with the authority it was
    /// configured with.
    /// </remarks>
    [Fact]
    public void Two_profiles_compose_two_different_issuers_from_one_address()
    {
        var idura = new IduraProfile([]).Root;
        var neb = new NetsEidBrokerProfile().Root;

        Assert.Equal(Address, Address + idura.Prefix);
        Assert.Equal($"{Address}/op", Address + neb.Prefix);
    }

    /// <summary>
    /// The negative control: the gate is the loaded profile's, and it refuses in both directions.
    /// </summary>
    /// <remarks>
    /// Without this the tests above would pass on an application that had simply stopped gating
    /// anything, which is the easier mistake and the more dangerous one - a stub looser than the
    /// broker passes a client the real thing would fail, and the failure arrives in production.
    /// So the default profile has to still refuse at the host root what Idura is served at.
    /// </remarks>
    [Fact]
    public async Task The_default_profile_still_refuses_what_it_always_did()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["StubId:PublicBaseUrl"] = Address });

        builder.Services.AddStubId(builder.Configuration);

        await using var app = builder.Build();

        app.UseStubId();
        await app.StartAsync(Ct);

        using var client = app.GetTestClient();

        Assert.Equal("neb", app.Services.GetRequiredService<IBrokerProfile>().Id.Broker);

        using var served = await client.GetAsync("/op/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        // Where Idura answers, and where this broker does not.
        using var atRoot = await client.GetAsync("/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotFound, atRoot.StatusCode);

        // And the strictness is the profile's too: this one refuses a trailing slash below its
        // root, where Idura tolerates one.
        using var trailing = await client.GetAsync("/op/.well-known/openid-configuration/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, trailing.StatusCode);

        // The base segment itself is compared ordinally, because a proxy selects the application
        // by it. Idura's root has no segment to get this wrong about.
        using var shouted = await client.GetAsync("/OP/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.NotFound, shouted.StatusCode);
    }

    /// <summary>An unknown profile name stops the instance rather than starting the wrong one.</summary>
    [Fact]
    public void A_profile_name_this_build_does_not_serve_is_refused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StubId:Profile"] = "signaturgruppen" })
            .Build();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => StubId.Server.BrokerProfiles.Select(configuration));

        Assert.Contains("signaturgruppen", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("neb", refusal.Message, StringComparison.Ordinal);
    }
}
