using System.Globalization;
using StubId.Server.Sessions;

using static StubId.Server.Admin.Markup;

namespace StubId.Server.Admin;

/// <summary>StubID's own pages, for a person rather than for a test.</summary>
/// <remarks>
/// Under <c>/_stubid</c> beside the control API, because the path gate in
/// <see cref="StubIdApplication" /> admits only that prefix and the broker's own. Putting these
/// under <c>/op</c> would mean declaring them as profile routes with a fabricated role, entering
/// them into the collision scan, and mixing StubID's own surface into the one it is emulating.
/// <para>
/// The pages read the services directly rather than calling the control API over HTTP. The
/// in-process host runs on a test server with no address to dial, so a self-call would work in the
/// container and be impossible in process - exactly the class of divergence between hosting modes
/// this project exists to catch. What the two doors share is <see cref="Approvals" />, which is
/// where the promise about one code path actually lives.
/// </para>
/// </remarks>
internal static class AdminUi
{
    private const string Root = "/_stubid/admin";

    public static void MapAdminUi(this WebApplication app)
    {
        app.MapGet(Root, (
            HttpContext http, SessionStore sessions, TimeProvider clock,
            string? state, string? clientId) =>
            Layout.Page(http, "Logins", Logins(sessions, clock, state, clientId)));

        app.MapGet($"{Root}/sessions/{{id}}", (
            HttpContext http, SessionStore sessions, Citizens citizens, TimeProvider clock,
            string id, string? problem) =>
            sessions.Find(id) is { } session
                ? Layout.Page(http, "Login", Login(session, citizens, clock, problem))
                : Results.NotFound());

        // The form is read rather than bound. Binding IFormCollection attaches anti-forgery
        // metadata to the endpoint, and the framework then refuses to serve it without
        // UseAntiforgery - which this instance deliberately does not have. The broker's own login
        // page reads its form the same way, for its own reasons, and the shapes match.
        app.MapPost($"{Root}/sessions/{{id}}/approve", async (
            HttpContext http, SessionStore sessions, Citizens citizens, string id) =>
        {
            var form = await http.Request.ReadFormAsync();

            return Answer(http, id, Approvals.Approve(
                sessions, citizens, id, form["citizen"].ToString(), "the admin page"));
        });

        app.MapPost($"{Root}/sessions/{{id}}/reject", async (
            HttpContext http, SessionStore sessions, string id) =>
        {
            var form = await http.Request.ReadFormAsync();

            return Answer(http, id, Approvals.Reject(
                sessions,
                id,
                form["errorCode"].ToString() is { Length: > 0 } code ? code : null,
                oauthError: null,
                "the admin page"));
        });
    }

    /// <summary>
    /// Post, redirect, get - so a reload does not decide the same login twice.
    /// </summary>
    /// <remarks>
    /// Losing the race is not an error here. The page the browser lands on shows what actually
    /// happened, which is the same answer the control API gives a caller that lost, in the shape a
    /// person can read.
    /// </remarks>
    private static IResult Answer(HttpContext http, string id, ApprovalOutcome outcome)
    {
        if (outcome.Result == ApprovalResult.NoSuchSession)
        {
            return Results.NotFound();
        }

        // A code rather than a message, so nothing a caller typed is reflected back into the page.
        var problem = outcome.Result == ApprovalResult.NoSuchCitizen ? "?problem=citizen" : "";

        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = $"{Root}/sessions/{Uri.EscapeDataString(id)}{problem}";

        return Results.Empty;
    }

    private static Html Logins(
        SessionStore sessions, TimeProvider clock, string? state, string? clientId)
    {
        // The same filter the control API answers to, and the same method: two copies of it would
        // be two things to keep agreeing.
        var matching = sessions.Matching(state, clientId);

        if (matching.Count == 0)
        {
            return H($"""
                <p class="empty">No logins yet. Start one from your application and it appears here.</p>
                <p class="dim">A login parks and waits for a decision only when the instance was
                started with <code>StubId__ApproveAutomatically=false</code>. Otherwise it is
                decided before you can see it, and what you get here is the record.</p>
                """);
        }

        return H($"""
            <table>
            <tr><th>Login</th><th>Client</th><th>State</th><th>Signed in as</th>
            <th>Started</th><th>Left</th></tr>
            {Join(matching.Select(session => H($"""
                <tr>
                <td><a href="{Root}/sessions/{session.Id}"><code>{Short(session.Id)}</code></a></td>
                <td><code>{Short(session.ClientId)}</code></td>
                <td>{session.State.ToString()}</td>
                <td>{session.CitizenId ?? "-"}</td>
                <td class="dim">{Moment(session.CreatedAt)}</td>
                <td class="dim">{Remaining(session, sessions.Timeout, clock)}</td>
                </tr>
                """)))}
            </table>
            """);
    }

    private static Html Login(AuthSession session, Citizens citizens, TimeProvider clock, string? problem)
    {
        var request = session.Request;

        return H($"""
            {Note(problem)}
            <table>
            {Row("Login", session.Id)}
            {Row("Client", session.ClientId)}
            {Row("State", session.State.ToString())}
            {Row("Signed in as", session.CitizenId)}
            {Row("Error code", session.ErrorCode)}
            {Row("OAuth error", session.OAuthError)}
            {Row("Started", Moment(session.CreatedAt))}
            {Row("Deadline", Moment(session.Deadline))}
            {Row("Decided", session.DecidedAt is { } at ? Moment(at) : null)}
            </table>

            <h2>What the client asked for</h2>
            <table>
            {Row("Redirect URI", request.RedirectUri)}
            {Row("Response type", request.ResponseType)}
            {Row("Response mode", request.ResponseMode)}
            {Row("Scope", request.Scope)}
            {Row("State", request.State)}
            {Row("Nonce", request.Nonce)}
            {Row("PKCE challenge", request.CodeChallenge)}
            {Row("PKCE method", request.CodeChallengeMethod)}
            {Row("Reference text", request.ReferenceText)}
            </table>

            {new Html(Endpoints.TransactionTextPanel(session))}

            <h2>Why it went the way it did</h2>
            {Ladder(session)}

            {Decide(session, citizens, clock)}
            """);
    }

    private static Html Ladder(AuthSession session)
    {
        if (session.Explanation.Count == 0)
        {
            return H($"""<p class="empty">Nothing has been decided yet.</p>""");
        }

        return H($"""
            <table>
            <tr><th>Tier</th><th>Rule</th><th>Outcome</th><th>Because</th></tr>
            {Join(session.Explanation.Select(step => H($"""
                <tr>
                <td>{Tier(step)}</td>
                <td>{step.Name}</td>
                <td>{step.Outcome}</td>
                <td class="dim">{step.Reason}</td>
                </tr>
                """)))}
            </table>
            """);
    }

    private static Html Decide(AuthSession session, Citizens citizens, TimeProvider clock)
    {
        if (session.IsDecided)
        {
            return H($"""
                <h2>Deciding it</h2>
                <p class="dim">This login is already decided, and a decision is written once.</p>
                """);
        }

        var options = Join(citizens.All
            .OrderBy(citizen => citizen.Id, StringComparer.Ordinal)
            .Select(citizen => H($"""<option value="{citizen.Id}">{citizen.Name}</option>""")));

        // The three the broker actually sends. Free text as well, because a suite exercising an
        // error path needs whichever code it is testing, not the three somebody thought of.
        var codes = Join(new[] { "mitid_user_aborted", "mitid_timeout", "mitid_identity_not_found" }
            .Select(code => H($"""<option value="{code}">{code}</option>""")));

        return H($"""
            <h2>Deciding it</h2>
            <p>This decision goes through the same store a test writes to, and shows up in the
            ladder above as <code>the admin page</code>. It has
            {Until(session.Deadline, clock)} left before it times out on its own.</p>
            <form method="post" action="{Root}/sessions/{session.Id}/approve" class="inline">
            <label>Sign in as <select name="citizen">{options}</select></label>
            <button type="submit">Approve</button>
            </form>
            <form method="post" action="{Root}/sessions/{session.Id}/reject" class="inline">
            <label>or refuse with <input name="errorCode" list="codes" value="mitid_user_aborted"
            size="24"></label>
            <datalist id="codes">{codes}</datalist>
            <button type="submit">Abort</button>
            </form>
            """);
    }

    // The last step stands outside the ladder: it is the one that runs when no tier had an opinion.
    private static string Tier(LadderStep step) => step.Tier == int.MaxValue
        ? "-"
        : step.Tier.ToString(CultureInfo.InvariantCulture);

    private static Html Note(string? problem) => problem == "citizen"
        ? H($"""<p><strong>That citizen is not on this instance, so nothing was decided.</strong></p>""")
        : Html.Empty;

    private static Html Row(string label, string? value) => value is { Length: > 0 }
        ? H($"<tr><th>{label}</th><td><code>{value}</code></td></tr>")
        : H($"""<tr><th>{label}</th><td class="dim">-</td></tr>""");

    // Enough of an identifier to tell two apart in a table, with the whole of it on its own page.
    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string Moment(DateTimeOffset when) =>
        when.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// How long this login has, and there are two answers because there are two deadlines.
    /// </summary>
    /// <remarks>
    /// An undecided login is counting down to its own deadline. An approved one has stopped
    /// counting that and started a second window, measured from the decision, in which the client
    /// has to come back for its code - and on a page somebody is demonstrating from, that is the
    /// number they actually need.
    /// </remarks>
    private static string Remaining(AuthSession session, TimeSpan window, TimeProvider clock) =>
        session.State switch
        {
            SessionState.AwaitingApproval => Until(session.Deadline, clock),
            SessionState.Approved when session.DecidedAt is { } decided =>
                $"{Until(decided + window, clock)} to collect",
            _ => "-",
        };

    private static string Until(DateTimeOffset deadline, TimeProvider clock)
    {
        var left = deadline - clock.GetUtcNow();

        return left <= TimeSpan.Zero
            ? "no time"
            : $"{(int)left.TotalMinutes}m {left.Seconds}s";
    }
}
