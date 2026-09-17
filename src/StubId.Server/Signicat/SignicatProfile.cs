using System.Reflection;
using StubId.Abstractions;
using StubId.Profiles;

namespace StubId.Server.Signicat;

/// <summary>
/// Signicat's Digital Trust Platform, as far as its unattended recordings settle it.
/// </summary>
/// <remarks>
/// Declared rather than emulated. Discovery and the key set are served, and everything else the
/// document advertises answers 501 with the section that says why. Nothing here completes a login,
/// because nothing has recorded one: that sitting needs a person in MitID's test tool, and until it
/// happens the claims, the request grammar and the way this broker refuses a login are unknown.
/// <para>
/// Internal, and reached by name through <see cref="BrokerProfiles" />. A public profile type would
/// be a shipped surface promising a broker this build only declares.
/// </para>
/// </remarks>
internal sealed class SignicatProfile : IBrokerProfile
{
    /// <summary>
    /// Which recordings are served, and by which build.
    /// </summary>
    /// <remarks>
    /// The first broker's version names the release that carried its sitting. No release carries
    /// this broker's recordings yet, so naming one would be a claim about a release that did not.
    /// It follows the build instead, derived rather than written down, which also means it cannot
    /// lead the build - the one thing a profile version may never do. The release that ships these
    /// recordings pins a literal here, the way the first broker's is pinned.
    /// </remarks>
    public ProfileId Id => new("signicat", Build);

    /// <summary>
    /// Two segments, both compared exactly, and a trailing slash refused.
    /// </summary>
    /// <remarks>
    /// Measured, and not the same rule as the first broker's: there the root segment is ordinal and
    /// everything below it is not. Here a request that capitalizes either segment is refused, and
    /// one that adds a trailing slash to a path that otherwise answers is refused as well.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/signicat/sandbox/CAP-005, fixtures/signicat/sandbox/CAP-006, "
                   + "fixtures/signicat/sandbox/CAP-007, fixtures/signicat/sandbox/CAP-008")]
    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.Divergent,
        Reason = "docs/brokers/signicat/divergences.md#the-404-outside-the-root")]
    [Fidelity(FidelityTier.Exact, FidelityProvenance.Assumed,
        Reason = "docs/brokers/signicat/divergences.md#awaiting-capture",
        AwaitingCapture = "An unattended batch that varies the case of a segment below the root, "
                          + "adds a trailing slash to an endpoint other than discovery, and posts "
                          + "to discovery. None of the three was probed, so what StubID does with "
                          + "them is the framework's answer rather than the broker's.")]
    public TenantRoot Root { get; } = new("auth/open", StringComparison.Ordinal, TrailingSlash.Refuse);

    public IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context) =>
        SignicatEndpoints.Declare();

    private static string Build =>
        typeof(SignicatProfile).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
}
