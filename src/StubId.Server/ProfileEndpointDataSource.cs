using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using StubId.Profiles;

namespace StubId.Server;

/// <summary>Carries a route's declaration to the matcher policy that enforces it.</summary>
public sealed record RouteRules(SegmentExactness Exactness, RouteRole Role);

/// <summary>
/// The routes come from the loaded profiles rather than from a fixed table.
/// </summary>
/// <remarks>
/// A data source rather than plain <c>Map</c> calls because tenants arrive at runtime, and
/// because two brokers disagree about paths at the root: one serves everything under a path
/// segment, the other serves it at the host root with a dynamic segment in front of
/// <c>.well-known</c>. Neither is expressible as a shared literal prefix.
/// </remarks>
public sealed class ProfileEndpointDataSource : EndpointDataSource
{
    private readonly object _gate = new();
    private List<Endpoint> _endpoints = [];
    private CancellationTokenSource _cancellation = new();
    private IChangeToken _token;

    public ProfileEndpointDataSource(IServiceProvider services)
    {
        _services = services;
        _token = new CancellationChangeToken(_cancellation.Token);
    }

    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

    public override IChangeToken GetChangeToken() => _token;

    /// <summary>
    /// Replaces the whole route set. Copy on write, then swap the change token and cancel the
    /// old one, in that order: cancelling first would have the framework rebuild against the
    /// list it is replacing.
    /// </summary>
    public void Load(IEnumerable<(IBrokerProfile Profile, ProfileContext Context, string MountPrefix)> tenants)
    {
        var built = new List<Endpoint>();

        foreach (var (profile, context, mountPrefix) in tenants)
        {
            foreach (var route in profile.DeclareRoutes(context))
            {
                built.Add(Build(route, mountPrefix, profile.Id));
            }
        }

        RouteCollisionScan.Verify(built);

        lock (_gate)
        {
            var previous = _cancellation;
            _endpoints = built;
            _cancellation = new CancellationTokenSource();
            _token = new CancellationChangeToken(_cancellation.Token);
            previous.Cancel();
            previous.Dispose();
        }
    }

    private readonly IServiceProvider _services;

    private Endpoint Build(RouteDeclaration route, string mountPrefix, ProfileId profile)
    {
        var text = string.IsNullOrEmpty(mountPrefix)
            ? $"/{route.Pattern}"
            : $"{mountPrefix.TrimEnd('/')}/{route.Pattern}";

        // Policy instances, not names: a name resolves through the application-wide constraint
        // map, where two profiles calling their constraint the same thing would collide.
        var pattern = RoutePatternFactory.Parse(
            text,
            defaults: null,
            parameterPolicies: route.ParameterPolicies.ToDictionary(p => p.Key, p => (object?)p.Value));

        // The framework's own factory, so a handler binds its services and parameters exactly
        // as a minimal-API handler does.
        var built = RequestDelegateFactory.Create(
            route.Handler,
            new RequestDelegateFactoryOptions { ServiceProvider = _services });

        var builder = new RouteEndpointBuilder(built.RequestDelegate, pattern, order: 0)
        {
            DisplayName = $"{profile} {route.Role} {text}",
        };

        foreach (var metadata in built.EndpointMetadata)
        {
            builder.Metadata.Add(metadata);
        }

        builder.Metadata.Add(new HttpMethodMetadata(route.Methods));
        builder.Metadata.Add(new RouteRules(route.Exactness, route.Role));

        return builder.Build();
    }
}
