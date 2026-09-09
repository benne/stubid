using Microsoft.AspNetCore.Http;

namespace StubId.Profiles;

/// <summary>
/// What a route answers when the emulated surface advertises it and this build does not
/// reproduce it.
/// </summary>
/// <remarks>
/// A discovery document served from a recording advertises everything the broker does, which is
/// the honest thing to serve and leaves a gap: some of what it names is not implemented. Left
/// alone those paths answer 404, and a 404 says "no such endpoint" when the truth is "that
/// endpoint exists and this emulator does not reproduce it". The two send whoever is reading a
/// log in different directions, and only one of them is true.
/// <para>
/// So the answer is 501, and it carries the reason as a link somebody can open. The reason is
/// held as a path from the root of the repository, because that is the form the fidelity ledger
/// serves and the build can check; it becomes a URL here, at the one point where it is read by
/// someone who is not looking at a clone.
/// </para>
/// </remarks>
public static class NotEmulated
{
    /// <summary>
    /// Where a reason resolves for a reader.
    /// </summary>
    /// <remarks>
    /// A blob URL rather than the documentation site: it survives a page being renamed or the
    /// site being restructured, it works for the same reader on a train, and it is the
    /// convention the README already uses for every link it carries.
    /// </remarks>
    public const string Documentation = "https://github.com/benne/stubid/blob/master/";

    /// <summary>Answers 501, naming the path and linking to why it is not reproduced.</summary>
    /// <param name="http">The request, which already says which endpoint was asked for.</param>
    /// <param name="reason">
    /// Where the decision is written down, as a path from the root of the repository, with the
    /// anchor of the section that explains it.
    /// </param>
    /// <param name="what">
    /// What was asked for, when the route knows something the path does not. A profile that
    /// carries a decoded segment can say so; left out, the path speaks for itself.
    /// </param>
    public static IResult Answer(HttpContext http, string reason, string? what = null) => Results.Json(
        new
        {
            error = "not_implemented",
            detail = $"StubID does not emulate {what ?? http.Request.Path.ToString()}.",
            reason = Documentation + reason,
        },
        statusCode: StatusCodes.Status501NotImplemented);
}
