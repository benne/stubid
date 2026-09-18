namespace StubId.Testing;

/// <summary>Where each broker this module knows serves its surface.</summary>
/// <remarks>
/// The in-process package asks the server, because it runs it. This module cannot: it starts an
/// image, and the assembly that knows every broker's root is inside that image rather than beside
/// this code. So the roots are written here, and <c>ModuleProfileTests</c> compares this list
/// against the server's own profiles on every build - which is what keeps a second copy from
/// quietly going stale, the thing that made asking the instance the right answer in the first place.
/// <para>
/// A name that is not here is not an error. The package and the image are versioned apart, so an
/// image can serve a broker published after the module that started it. What the module cannot do
/// is guess where that broker's surface sits, which is why <see cref="Of" /> answers null rather
/// than a default.
/// </para>
/// </remarks>
internal static class BrokerRoots
{
    /// <summary>The broker an instance serves when nothing chose one.</summary>
    public const string Default = "neb";

    private static readonly Dictionary<string, string> Roots = new(StringComparer.Ordinal)
    {
        [Default] = "op",
        ["signicat"] = "auth/open",
    };

    /// <summary>The names this module can compose an authority for, in a message that lists them.</summary>
    public static IReadOnlyCollection<string> Known => Roots.Keys;

    /// <summary>
    /// Where the default broker serves, for an instance too old to name a root of its own.
    /// </summary>
    public static string OfDefault => Roots[Default];

    /// <summary>
    /// The root the named broker serves under, or null when this module has not heard of it.
    /// </summary>
    /// <remarks>
    /// An empty name is the default rather than an unknown one, because that is what an instance
    /// nobody chose a broker for is serving.
    /// </remarks>
    public static string? Of(string? profile) =>
        Roots.TryGetValue(
            string.IsNullOrWhiteSpace(profile) ? Default : profile.Trim(), out var root)
            ? root
            : null;
}
