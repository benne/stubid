using StubId.Profiles;

namespace StubId.Server;

/// <summary>
/// Signaturgruppen Broker, as recorded.
/// </summary>
/// <remarks>
/// Only the route table lives behind the seam so far. Claim composition, error envelopes, the
/// key roster and the request grammar are still engine code, and stay there until a second
/// profile actually needs them to differ — an abstraction with one implementation is a guess,
/// and the recordings that would justify each of those shapes for a second broker do not
/// exist yet.
/// </remarks>
public sealed class NetsEidBrokerProfile : IBrokerProfile
{
    public ProfileId Id => new("neb", "2026.09.1");

    /// <summary>
    /// Probed against pre-production rather than assumed: the segment itself is compared ordinally
    /// because a reverse proxy selects the application by it, and a trailing slash below it is
    /// refused.
    /// </summary>
    public TenantRoot Root { get; } = new("op", StringComparison.Ordinal, TrailingSlash.Refuse);

    public IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context) => Endpoints.Declare();
}
