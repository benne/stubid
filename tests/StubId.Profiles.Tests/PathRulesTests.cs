using StubId.Server;

namespace StubId.Profiles.Tests;

/// <summary>
/// The gate in front of routing, for a root that is more than one segment.
/// </summary>
/// <remarks>
/// The first broker's root is one segment and the spike's is none, so a root of two was expressible
/// and untested until a second broker needed one. What it has to get right is the boundary: a
/// prefix that matches as text is not a root that matches as a path, and `/auth/openid` is the
/// case that tells them apart.
/// </remarks>
public class PathRulesTests
{
    private static readonly TenantRoot Signicat =
        new("auth/open", StringComparison.Ordinal, TrailingSlash.Refuse);

    [Theory]
    [InlineData("/auth/open/.well-known/openid-configuration")]
    [InlineData("/auth/open/connect/authorize")]
    [InlineData("/auth/open")]
    public void A_path_under_a_two_segment_root_is_let_through(string path)
    {
        Assert.True(new PathRules(Signicat).Accepts(path));
    }

    /// <summary>
    /// What the gate refuses, and why each one is here.
    /// </summary>
    /// <remarks>
    /// The capitalized segments are recorded refusals rather than a choice. The trailing slash is
    /// recorded too, and answers an empty 404 where the broker's own edge answers a page for the
    /// rows below it. `auth` alone and `/auth/openid` are the boundary: the first is a prefix of
    /// the root and the second has the root as a prefix, and neither is the root.
    /// </remarks>
    [Theory]
    [InlineData("/Auth/open/.well-known/openid-configuration")]
    [InlineData("/auth/Open/.well-known/openid-configuration")]
    [InlineData("/auth/open/.well-known/openid-configuration/")]
    [InlineData("/auth/openid/connect/authorize")]
    [InlineData("/auth")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/op/.well-known/openid-configuration")]
    public void A_path_the_broker_refuses_never_reaches_routing(string path)
    {
        Assert.False(new PathRules(Signicat).Accepts(path));
    }

    /// <summary>A root is written without slashes, and says so rather than composing a wrong prefix.</summary>
    [Theory]
    [InlineData("/auth/open")]
    [InlineData("auth/open/")]
    public void A_root_written_with_a_slash_is_refused(string segments)
    {
        Assert.Throws<ArgumentException>(() =>
            new TenantRoot(segments, StringComparison.Ordinal, TrailingSlash.Refuse));
    }

    [Fact]
    public void A_two_segment_root_composes_one_prefix()
    {
        Assert.Equal("/auth/open", Signicat.Prefix);
    }
}
