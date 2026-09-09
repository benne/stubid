using System.Globalization;
using Microsoft.AspNetCore.Http;
using StubId.Profiles;

namespace StubId.Server;

/// <summary>A route on its way out, and when it started being on its way out.</summary>
/// <param name="Since">
/// When the route was deprecated, which may be ahead of now: a release announcing a deprecation
/// that takes effect later is the case the field is for.
/// </param>
/// <param name="Reason">
/// Where the decision is written down, as a path from the root of the repository with the anchor
/// of the section that explains it. The same form the fidelity ledger holds, and for the same
/// reason: the build can check that it points somewhere.
/// </param>
/// <param name="Sunset">
/// When the route is expected to stop answering, where that is known. Usually it is not - see the
/// remarks on <see cref="Deprecation.Deprecated" />.
/// </param>
internal sealed record DeprecationNotice(
    DateTimeOffset Since,
    string Reason,
    DateTimeOffset? Sunset = null)
{
    /// <summary>Where the reason resolves for a reader who is not looking at a clone.</summary>
    public string Explanation => NotEmulated.Documentation + Reason;
}

/// <summary>
/// Saying that a route is going away to a caller that is not a .NET compiler.
/// </summary>
/// <remarks>
/// The compatibility statement promises that a renamed control route keeps answering for one
/// release before it stops. A .NET caller collects that promise as <c>[Obsolete]</c> and a build
/// warning. Everyone else collected nothing: the one control route this project has retired - the
/// British spelling of <c>/behaviors/enqueue</c>, dropped in 2026.09.3 - gave a Node, Spring or
/// curl caller no in-band signal at all before it began answering 404, and the release that was
/// meant as their grace period was a release in which nothing told them.
/// <para>
/// Headers and metadata, deliberately. The headers are what a caller reads. The metadata is what
/// the build reads, so that removing a route without having marked it first is a failure here
/// rather than a discovery in somebody's pipeline.
/// </para>
/// </remarks>
internal static class Deprecation
{
    /// <summary>Marks a route as going away, and says so on every response it gives.</summary>
    /// <remarks>
    /// <paramref name="sunset" /> is optional because this project has no release calendar. A
    /// date invented to fill the header in would be a promise about when a release happens, which
    /// is a promise nothing here can keep; RFC 8594 asks for a timestamp in the future, not for
    /// one that exists. So a sunset is sent when a removal has actually been scheduled and left
    /// out otherwise, and the deprecation is announced either way.
    /// </remarks>
    public static RouteHandlerBuilder Deprecated(
        this RouteHandlerBuilder builder,
        DateTimeOffset since,
        string reason,
        DateTimeOffset? sunset = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var notice = new DeprecationNotice(since, reason, sunset);

        return builder
            .WithMetadata(notice)
            .AddEndpointFilter(async (context, next) =>
            {
                // Before the handler, not after: a handler that writes the response itself has
                // already sent the headers by the time the filter gets its turn back.
                Announce(context.HttpContext.Response, notice);

                return await next(context);
            });
    }

    /// <summary>The two header fields the RFCs define, plus the link that explains them.</summary>
    /// <remarks>
    /// <c>Deprecation</c> is an item structured header field whose value is a Date (RFC 9745),
    /// which is an <c>@</c> and then seconds since the epoch - not the HTTP-date that
    /// <c>Sunset</c> takes, and not the bare <c>true</c> that an earlier draft allowed. Getting
    /// these two the same way round is the whole content of the field, so they are written here
    /// once rather than at each route.
    /// </remarks>
    internal static void Announce(HttpResponse response, DeprecationNotice notice)
    {
        response.Headers["Deprecation"] =
            "@" + notice.Since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        if (notice.Sunset is { } sunset)
        {
            // RFC 8594: an HTTP-date, which is the "R" format and always in GMT.
            response.Headers["Sunset"] =
                sunset.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        }

        // RFC 9745 names this relation for the document a caller should read. Appended rather
        // than assigned, because a response is allowed to carry more than one link.
        response.Headers.Append(
            "Link", $"<{notice.Explanation}>; rel=\"deprecation\"; type=\"text/html\"");
    }
}
