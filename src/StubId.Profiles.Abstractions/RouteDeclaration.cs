using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using StubId.Abstractions;

namespace StubId.Profiles;

/// <summary>What a route is for, so the engine can find one without knowing its path.</summary>
public readonly record struct RouteRole(string Name)
{
    public static readonly RouteRole Discovery = new("discovery");
    public static readonly RouteRole Jwks = new("jwks");
    public static readonly RouteRole Authorize = new("authorize");
    public static readonly RouteRole Token = new("token");
    public static readonly RouteRole UserInfo = new("userinfo");
    public static readonly RouteRole Par = new("par");
    public static readonly RouteRole ErrorPage = new("error-page");

    /// <summary>A route only one broker has. The name is the profile's to choose.</summary>
    public static RouteRole Extra(string name) => new($"extra:{name}");

    public override string ToString() => Name;
}

/// <summary>Whether a path with a trailing slash reaches the route.</summary>
public enum TrailingSlash
{
    /// <summary>404. What Nets eID Broker does.</summary>
    Refuse,

    /// <summary>Matches anyway. What Idura does.</summary>
    Tolerate,
}

/// <summary>
/// How each literal segment of a route is compared, and what a trailing slash does.
/// </summary>
/// <remarks>
/// Not one rule for the whole application, because the two brokers differ and both are right
/// about themselves. Being stricter than the broker fails a client that works against it;
/// being looser passes one that does not. Probed, not assumed: Nets eID Broker serves
/// <c>/op/.WELL-KNOWN/openid-configuration</c> but refuses <c>/OP/.well-known/...</c>, because
/// a proxy selects the application by a case-sensitive prefix and the application beneath it
/// matches case-insensitively.
/// </remarks>
/// <param name="LiteralSegments">
/// One comparison per literal segment, in order. A dynamic segment carries none: its
/// strictness belongs to its parameter policy.
/// </param>
public sealed record SegmentExactness(
    IReadOnlyList<StringComparison> LiteralSegments,
    TrailingSlash TrailingSlash)
{
    public static SegmentExactness Uniform(StringComparison comparison, TrailingSlash trailingSlash) =>
        new(Array.Empty<StringComparison>(), trailingSlash) { Fallback = comparison };

    /// <summary>The Nets eID Broker shape: the first segment ordinal, everything after it not.</summary>
    public static SegmentExactness FirstOrdinalThenInsensitive(TrailingSlash trailingSlash) =>
        new(new[] { StringComparison.Ordinal }, trailingSlash)
        {
            Fallback = StringComparison.OrdinalIgnoreCase,
        };

    /// <summary>Applied to any literal segment beyond those listed.</summary>
    public StringComparison Fallback { get; init; } = StringComparison.OrdinalIgnoreCase;

    public StringComparison For(int literalIndex) =>
        literalIndex < LiteralSegments.Count ? LiteralSegments[literalIndex] : Fallback;
}

/// <summary>What answers a request that reaches a route by the wrong method.</summary>
public abstract record WrongMethod
{
    /// <summary>The framework's 405, with an Allow header.</summary>
    public sealed record Default : WrongMethod;

    /// <summary>Let the route's own handler answer, which is how one broker reports it.</summary>
    public sealed record RouteAnyway : WrongMethod;

    public static readonly WrongMethod Standard = new Default();
    public static readonly WrongMethod HandledByRoute = new RouteAnyway();
}

/// <summary>
/// One route a profile serves, declared relative to the tenant's root.
/// </summary>
/// <remarks>
/// Patterns are relative and carry no leading slash, because the host composes the tenant's
/// mount prefix. Nets eID Broker's <c>op</c> is the first segment of its own pattern rather
/// than a mount prefix — which is what lets a document served from under a path segment
/// declare an issuer without one, as Idura's does.
/// </remarks>
/// <param name="ParameterPolicies">
/// Policy instances rather than names. A name would go through the application-wide constraint
/// map, where two profiles calling their constraint the same thing would collide.
/// </param>
/// <param name="Handler">
/// Bound by the framework's own request-delegate factory, so a handler declares the services
/// and parameters it wants exactly as a minimal-API handler does. Anything a middleware can do,
/// a handler can do: this is the escape hatch, which is why there is no declarative rule
/// language for profiles.
/// </param>
public sealed record RouteDeclaration(
    string Pattern,
    IReadOnlyList<string> Methods,
    RouteRole Role,
    Delegate Handler)
{
    public IReadOnlyDictionary<string, IParameterPolicy> ParameterPolicies { get; init; } =
        new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal);

    public SegmentExactness Exactness { get; init; } =
        SegmentExactness.Uniform(StringComparison.OrdinalIgnoreCase, TrailingSlash.Tolerate);

    public WrongMethod WrongMethod { get; init; } = WrongMethod.Standard;

    public FidelityAttribute? Fidelity { get; init; }
}
