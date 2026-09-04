using static StubId.Server.Admin.Markup;

namespace StubId.Server.Admin;

/// <summary>The shell every admin page is rendered into.</summary>
/// <remarks>
/// Deliberately not shared with <see cref="Endpoints" />'s pages. Those are emulated surface: their
/// text is pinned by tests, the browser matrix drives them, and the login page is constrained by
/// TRADEMARKS.md to look like nobody's authenticator. Giving that page an administrative navigation
/// would also let somebody mid-login wander into the controls, which is the opposite of what it is
/// for.
/// </remarks>
internal static class Layout
{
    /// <summary>
    /// The stylesheet, as a constant rather than a file.
    /// </summary>
    /// <remarks>
    /// <see cref="StubId.Server" /> is packable on purpose, so that the in-process host depends on
    /// this assembly rather than a copy of it; a <c>wwwroot</c> would not travel in the package,
    /// and the image build copies only <c>src/</c>. A constant needs no build target, no route and
    /// no caching decision, and it lands in a <c>.cs</c> file, which the repository's tree-wide
    /// sweeps already read.
    /// </remarks>
    private const string Style = """
        :root{color-scheme:light dark;--line:#d0d0d6;--dim:#666;--bg:#fff;--fg:#111;--head:#f6f6f8}
        @media(prefers-color-scheme:dark){
        :root{--line:#33343a;--dim:#9a9aa2;--bg:#16171a;--fg:#e8e8ea;--head:#1e1f24}}
        *{box-sizing:border-box}
        body{font:14px/1.5 system-ui,sans-serif;margin:0;background:var(--bg);color:var(--fg)}
        header{border-bottom:1px solid var(--line);padding:.75rem 1.5rem}
        header b{font-weight:600}
        header p{margin:.25rem 0 0;color:var(--dim);font-size:.85rem}
        nav{display:flex;gap:1rem;padding:.5rem 1.5rem;border-bottom:1px solid var(--line)}
        nav a{color:inherit;text-decoration:none;padding:.15rem 0;border-bottom:2px solid transparent}
        nav a[aria-current]{border-bottom-color:currentColor;font-weight:600}
        main{padding:1.5rem;max-width:70rem}
        h1{font-size:1.35rem;margin:0 0 1rem}
        h2{font-size:1rem;margin:1.75rem 0 .5rem}
        table{border-collapse:collapse;width:100%;margin:.5rem 0}
        th,td{text-align:left;padding:.4rem .75rem .4rem 0;border-bottom:1px solid var(--line);
        vertical-align:top}
        th{font-weight:600;background:var(--head)}
        td.dim,.dim{color:var(--dim)}
        code,pre{font:13px/1.45 ui-monospace,monospace}
        pre{background:var(--head);padding:.75rem;overflow-x:auto;border:1px solid var(--line)}
        .empty{color:var(--dim);font-style:italic}
        form.inline{display:inline}
        button{font:inherit;padding:.3rem .8rem}
        """;

    /// <summary>
    /// The line that says what this is, on every page.
    /// </summary>
    /// <remarks>
    /// TRADEMARKS.md undertakes that no page of StubID's suggests a real authentication took
    /// place. The login page carries its own version of this because a person is being asked to
    /// approve something there; these pages carry it because somebody who found one should not
    /// have to work out what they are looking at.
    /// </remarks>
    private const string Standing =
        "StubID is an emulator. No identity is verified here, and nothing it issues has any legal effect.";

    private static readonly (string Path, string Label)[] Sections =
    [
        ("/_stubid/admin", "Logins"),
    ];

    /// <summary>A rendered page, with the headers an administrative page wants.</summary>
    public static IResult Page(HttpContext http, string title, Html body)
    {
        // The state on these pages changes underneath the reader, and a back button that returns a
        // cached table with a live Approve button on it is a genuine annoyance rather than a
        // theoretical one.
        http.Response.Headers.CacheControl = "no-store";

        // A session id travels in the path here, and a Referer would carry it off the instance.
        http.Response.Headers["Referrer-Policy"] = "same-origin";

        return Results.Text(Document(http, title, body).Value, "text/html; charset=utf-8");
    }

    private static Html Document(HttpContext http, string title, Html body) => H($"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{title} - StubID</title>
        <style>{new Html(Style)}</style>
        </head><body>
        <header><b>StubID</b><p>{Standing}</p></header>
        {Navigation(http.Request.Path)}
        <main><h1>{title}</h1>{body}</main>
        </body></html>
        """);

    private static Html Navigation(PathString here) => H($"""
        <nav>{Join(Sections.Select(section => H(
            $"""<a href="{section.Path}"{Current(here, section.Path)}>{section.Label}</a>""")))}</nav>
        """);

    // Marked on the section that owns the page rather than only on an exact match, so a login's own
    // page still shows which list it came from.
    private static Html Current(PathString here, string section) =>
        here.StartsWithSegments(section) ? new Html(" aria-current=\"page\"") : Html.Empty;
}
