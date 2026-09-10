namespace StubId.Profiles;

/// <summary>
/// Where a broker's surface begins under the host, and how a path is gated before routing.
/// </summary>
/// <remarks>
/// <para>
/// Not a mount prefix. A profile's routes still declare their own patterns in full, including the
/// segment named here — Nets eID Broker's <c>op</c> is the first segment of its own pattern rather
/// than something the host puts in front of it, which is what lets a document served from under a
/// path segment declare an issuer without one. What this adds is the two things a route table
/// cannot say about itself: what the issuer ends with, and which paths reach the router at all.
/// </para>
/// <para>
/// The gate matters because it runs before routing. The framework would otherwise match a path the
/// broker refuses, and being looser than the broker passes a client the real thing would fail —
/// the same false pass this project exists to prevent, wearing different clothes.
/// </para>
/// </remarks>
/// <param name="Segments">
/// The path the surface sits under, relative and without slashes at either end, exactly as a route
/// pattern is declared. Empty for a broker that serves at the host root.
/// </param>
/// <param name="Comparison">
/// How <paramref name="Segments" /> itself is compared. Nets eID Broker's is ordinal because a
/// reverse proxy selects the application by it; what sits below it is the router's business and is
/// governed by each route's own <see cref="SegmentExactness" />.
/// </param>
/// <param name="TrailingSlash">Whether a path below the root may end in one.</param>
public sealed record TenantRoot(
    string Segments,
    StringComparison Comparison,
    TrailingSlash TrailingSlash)
{
    /// <summary>The whole host, with no path segment of its own. What Idura serves at.</summary>
    public static readonly TenantRoot HostRoot =
        new("", StringComparison.OrdinalIgnoreCase, TrailingSlash.Tolerate);

    /// <summary>
    /// What an issuer ends with and what the gate matches: the segments with a leading slash, or
    /// nothing at all at the host root.
    /// </summary>
    /// <remarks>
    /// Held as one string rather than composed at each call site. It is concatenated onto the
    /// public base URL to form an issuer that client libraries compare character for character,
    /// and a second spelling of the rule is a second chance to get a slash wrong.
    /// </remarks>
    public string Prefix { get; } = Segments.Length == 0 ? "" : "/" + Segments;

    /// <summary>Refuses a shape that would produce a malformed issuer.</summary>
    /// <remarks>
    /// On the record rather than at the point of use, because the failure it prevents is an issuer
    /// with a doubled or trailing slash in it, and that is discovered by a client library long
    /// after the profile that caused it has stopped being the obvious suspect.
    /// </remarks>
    public string Segments { get; } =
        Segments.StartsWith('/') || Segments.EndsWith('/')
            ? throw new ArgumentException(
                $"A tenant root is declared the way a route pattern is, with no slash at either "
                + $"end. Got '{Segments}'.",
                nameof(Segments))
            : Segments;
}
