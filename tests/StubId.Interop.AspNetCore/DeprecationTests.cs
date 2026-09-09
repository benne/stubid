using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What a caller that is not a .NET compiler is told before a route stops answering.
/// </summary>
/// <remarks>
/// Nothing in this build is deprecated, which is exactly why these exist. The one route this
/// project has retired went without any of this, and a mechanism first exercised on the day it is
/// needed is one whose header formats nobody has checked. Both fields are easy to get the wrong
/// way round - one is seconds since the epoch and the other is an HTTP-date - so the shapes are
/// pinned here against the examples in the two RFCs rather than against what seemed right.
/// </remarks>
public class DeprecationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reason = "docs/compatibility.md#deprecation-and-what-one-release-of-notice-is-worth";

    /// <summary>The instant RFC 9745's own example encodes, so the expected string is theirs.</summary>
    private static readonly DateTimeOffset Since = DateTimeOffset.FromUnixTimeSeconds(1688169599);

    /// <summary>An application with one route, on its way out.</summary>
    private static WebApplication Retiring(DateTimeOffset? sunset = null)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        var app = builder.Build();

        app.MapGet("/going", () => Results.Ok()).Deprecated(Since, Reason, sunset);

        return app;
    }

    [Fact]
    public async Task A_deprecated_route_dates_its_deprecation_the_way_the_field_is_defined()
    {
        await using var app = Retiring();
        await app.StartAsync(Ct);

        using var response = await app.GetTestClient().GetAsync("/going", Ct);

        // An item structured header field whose value is a Date: "@" and then seconds since the
        // epoch. Not the HTTP-date Sunset takes, and not the bare "true" an earlier draft had.
        Assert.Equal("@1688169599", Assert.Single(response.Headers.GetValues("Deprecation")));
    }

    [Fact]
    public async Task A_deprecated_route_links_to_where_the_decision_is_written()
    {
        await using var app = Retiring();
        await app.StartAsync(Ct);

        using var response = await app.GetTestClient().GetAsync("/going", Ct);

        var link = Assert.Single(response.Headers.GetValues("Link"));

        Assert.Equal(
            $"<https://github.com/benne/stubid/blob/master/{Reason}>; rel=\"deprecation\"; type=\"text/html\"",
            link);
    }

    /// <summary>A removal nobody has scheduled says nothing about when it happens.</summary>
    /// <remarks>
    /// The header is not merely omitted for tidiness. RFC 8594 asks for a timestamp in the
    /// future, and this project has no release calendar to draw one from, so a date put there to
    /// fill the field in would be a promise about when a release happens rather than a fact.
    /// </remarks>
    [Fact]
    public async Task A_removal_with_no_date_set_sends_no_sunset()
    {
        await using var app = Retiring();
        await app.StartAsync(Ct);

        using var response = await app.GetTestClient().GetAsync("/going", Ct);

        Assert.False(response.Headers.Contains("Sunset"));
    }

    [Fact]
    public async Task A_scheduled_removal_says_when_in_the_format_that_field_takes()
    {
        var sunset = DateTimeOffset.FromUnixTimeSeconds(1719791999);

        await using var app = Retiring(sunset);
        await app.StartAsync(Ct);

        using var response = await app.GetTestClient().GetAsync("/going", Ct);

        var sent = Assert.Single(response.Headers.GetValues("Sunset"));

        // An HTTP-date, which is fixed-width, always in GMT, and reads back as the same instant.
        Assert.EndsWith(" GMT", sent, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParseExact(
            sent, "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out var read));
        Assert.Equal(sunset, read);
    }

    /// <summary>The build can see it too, which is the half a caller never notices.</summary>
    /// <remarks>
    /// Headers alone would leave the promise where it was: written down and unenforced. The
    /// notice is endpoint metadata as well, so that a route removed without one having been
    /// attached is something a test can fail on rather than something a consumer discovers.
    /// </remarks>
    [Fact]
    public async Task The_notice_is_on_the_endpoint_and_not_only_on_the_response()
    {
        await using var app = Retiring();
        await app.StartAsync(Ct);

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/going");

        var notice = endpoint.Metadata.GetMetadata<DeprecationNotice>();

        Assert.NotNull(notice);
        Assert.Equal(Since, notice.Since);
        Assert.Equal(Reason, notice.Reason);
        Assert.Null(notice.Sunset);
    }

    /// <summary>
    /// Whatever this build deprecates points at a document a reader can open.
    /// </summary>
    /// <remarks>
    /// Empty today, and that is the point: the link is the only thing in the response that tells
    /// a caller what to do instead, so a reason naming a page that was renamed is worse than no
    /// header at all. The fidelity ledger is held to the same rule for the same reason, and this
    /// is where the rule binds the next route rather than the next annotation.
    /// </remarks>
    [Fact]
    public void Every_deprecation_in_this_build_points_at_a_document_that_exists()
    {
        using var factory = new WebApplicationFactory<Program>();

        var dangling = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<DeprecationNotice>())
            .Where(notice => notice is not null)
            .Where(notice => !File.Exists(Path.Combine(Root(), notice!.Reason.Split('#')[0])))
            .Select(notice => notice!.Reason)
            .ToList();

        Assert.True(dangling.Count == 0,
            "These deprecations point at a document that is not there: "
            + string.Join(", ", dangling));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    /// <summary>Nothing that is not deprecated says it is.</summary>
    [Fact]
    public async Task A_route_that_is_staying_says_nothing()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        await using var app = builder.Build();

        app.MapGet("/staying", () => Results.Ok());

        await app.StartAsync(Ct);

        using var response = await app.GetTestClient().GetAsync("/staying", Ct);

        Assert.False(response.Headers.Contains("Deprecation"));
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.False(response.Headers.Contains("Link"));
    }
}
