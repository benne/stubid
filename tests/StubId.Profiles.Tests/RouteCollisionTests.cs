using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.DependencyInjection;
using StubId.Profiles;
using StubId.Profiles.Idura;
using StubId.Server;

namespace StubId.Profiles.Tests;

/// <summary>
/// A route set that would be ambiguous must stop the boot.
/// </summary>
/// <remarks>
/// Left alone it does not fail fast: the matcher is built lazily on the first request, so two
/// profiles declaring the same path start silently and then throw on every request afterwards.
/// The compiler's duplicate-route analyser cannot see it either, because it only looks at
/// literal registrations in source and these come from a profile.
/// </remarks>
public class RouteCollisionTests
{
    private sealed record Fake(string Name, params RouteDeclaration[] Routes) : IBrokerProfile
    {
        public ProfileId Id => new(Name, "test");

        public IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context) => Routes;
    }

    private static RouteDeclaration Route(
        string pattern, string method = "GET",
        IReadOnlyDictionary<string, IParameterPolicy>? policies = null) =>
        new(pattern, [method], RouteRole.Extra(pattern), () => Results.Ok())
        {
            ParameterPolicies = policies ?? new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal),
        };

    private static void Load(params IBrokerProfile[] profiles)
    {
        // The real profile's handlers ask for the services they need, and the framework's
        // factory binds them, so the provider has to be able to answer.
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Documents>();
        services.AddSingleton<Keys>();
        services.AddSingleton<BrokerState>();
        services.AddSingleton<Tokens>();
        services.AddSingleton<StubId.Server.Sessions.Citizens>();
        services.AddSingleton<StubId.Server.Sessions.EnqueuedDecisions>();
        services.AddSingleton(sp => new StubId.Server.Sessions.Ladder(
        [
        ]));
        services.AddSingleton(sp => new StubId.Server.Sessions.SessionStore(
            TimeProvider.System, sp.GetRequiredService<StubId.Server.Sessions.Ladder>()));
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var source = new ProfileEndpointDataSource(services.BuildServiceProvider());
        var context = new ProfileContext("https://example.test", "https://example.test");

        source.Load(profiles.Select(p => (p, context, "")));
    }

    [Fact]
    public void Two_profiles_claiming_one_path_refuse_to_start()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Load(new Fake("a", Route("oauth2/token")), new Fake("b", Route("oauth2/token"))));

        Assert.Contains("same request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_path_answering_different_methods_is_not_a_collision()
    {
        Load(new Fake("a", Route("oauth2/token", "GET"), Route("oauth2/token", "POST")));
    }

    [Fact]
    public void Two_dynamic_segments_with_different_policies_are_not_a_collision()
    {
        // The Idura route the seam exists to support sits at a position where another profile
        // might legitimately put a differently-constrained parameter, so a check keyed on
        // shape alone would reject exactly the case it was built for.
        var acr = new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal)
        {
            ["p"] = new AcrSegmentPolicy(["urn:grn:authn:dk:mitid:low"]),
        };
        var guid = new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal)
        {
            ["p"] = new GuidRouteConstraint(),
        };

        Load(new Fake("a", Route("{p}/thing", "GET", acr)), new Fake("b", Route("{p}/thing", "GET", guid)));
    }

    [Fact]
    public void The_working_profiles_load_together_without_colliding()
    {
        // Nets eID Broker serves everything under /op and Idura at the host root, so they can
        // share one host. That they do not collide is a fact worth pinning rather than assuming.
        Load(new NetsEidBrokerProfile(), new IduraProfile([new IduraClient("urn:idura:dev")]));
    }
}
