using Microsoft.AspNetCore.Http;

namespace StubId.Profiles;

/// <summary>Which broker, at which recorded version.</summary>
public readonly record struct ProfileId(string Broker, string Version)
{
    public override string ToString() => $"{Broker}@{Version}";
}

/// <summary>What a profile is told about the tenant it is serving.</summary>
/// <param name="Issuer">
/// Emitted verbatim: as discovery's issuer, as <c>iss</c> in every token, and as the
/// authorization-response parameter. Never derived from the request, never normalized, never
/// given a trailing slash. One broker's issuer carries a path segment and the other's does
/// not, and both are compared character for character by client libraries.
/// </param>
/// <param name="PublicBaseUrl">What absolute URLs inside served documents are built from.</param>
public sealed record ProfileContext(string Issuer, string PublicBaseUrl);

/// <summary>
/// One broker's personality: what it serves, where, and how strictly it matches.
/// </summary>
/// <remarks>
/// Deliberately small for now. The seam was designed against two brokers rather than one, and
/// the parts that only a second implementation would exercise — claim composition, error
/// envelopes, key rosters, request grammar — stay in the engine until a second profile
/// actually needs them. An abstraction with one implementation is a guess.
/// </remarks>
public interface IBrokerProfile
{
    ProfileId Id { get; }

    /// <summary>
    /// Where this broker's surface begins under the host, and what its issuer ends with.
    /// </summary>
    /// <remarks>
    /// Declared rather than inferred from the route table. Two of the things that depend on it —
    /// the path gate that runs before routing, and the issuer — are not routes and cannot read
    /// one, and the first broker got both by having <c>/op</c> written into the engine in five
    /// places.
    /// </remarks>
    TenantRoot Root { get; }

    /// <summary>
    /// The routes this profile serves, relative to the tenant root. The host composes any
    /// mount prefix and registers them.
    /// </summary>
    IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context);
}
