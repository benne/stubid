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
    public ProfileId Id => new("neb", "2026.08.1");

    public IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context) => Endpoints.Declare();
}
