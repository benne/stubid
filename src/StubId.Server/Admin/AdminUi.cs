using System.Globalization;
using StubId.Server.Sessions;
using StubId.Wire;

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
            Layout.Page(http, "Logins", Logins(http, sessions, clock, state, clientId)));

        // The rows on their own, which is what the page's own script asks for every couple of
        // seconds. A fragment rather than JSON: the table is rendered in one place, by the code
        // that already knows how to shorten an id and count down two different deadlines, and a
        // second renderer in JavaScript would be a second set of those rules to keep agreeing.
        app.MapGet($"{Root}/rows", (
            HttpContext http, SessionStore sessions, TimeProvider clock,
            string? state, string? clientId) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            return Results.Text(
                Rows(sessions, clock, state, clientId).Value, "text/html; charset=utf-8");
        });

        app.MapGet($"{Root}/sessions/{{id}}", (
            HttpContext http, SessionStore sessions, Citizens citizens, TimeProvider clock,
            string id, string? problem) =>
            sessions.Find(id) is { } session
                ? Layout.Page(http, "Login", Login(session, citizens, clock, problem))
                : Results.NotFound());

        app.MapGet($"{Root}/citizens", (HttpContext http, Citizens citizens, string? problem) =>
            Layout.Page(http, "People", People(citizens, problem)));

        app.MapGet($"{Root}/behaviour", (
            HttpContext http, EnqueuedDecisions queue, Citizens citizens, string? problem) =>
            Layout.Page(http, "Queued decisions", Behaviours(queue, citizens, problem)));

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

        app.MapPost($"{Root}/citizens", async (HttpContext http, Citizens citizens) =>
        {
            var form = await http.Request.ReadFormAsync();
            var name = form["name"].ToString();

            // Refused rather than invented. A page that quietly names somebody "Unnamed" is worse
            // than one that says the field was empty.
            if (name.Length == 0)
            {
                return See(http, $"{Root}/citizens", "name");
            }

            if (!DateOnly.TryParse(
                form["dateOfBirth"].ToString(), CultureInfo.InvariantCulture, out var born))
            {
                return See(http, $"{Root}/citizens", "date");
            }

            citizens.Create(
                Optional(form, "id"),
                name,
                born,
                string.Equals(form["gender"].ToString(), "male", StringComparison.OrdinalIgnoreCase)
                    ? Gender.Male
                    : Gender.Female,
                Optional(form, "userName"),
                Optional(form, "rule"));

            return See(http, $"{Root}/citizens");
        });

        app.MapPost($"{Root}/citizens/{{id}}/rule", async (
            HttpContext http, Citizens citizens, string id) =>
        {
            var form = await http.Request.ReadFormAsync();

            // An empty box clears the rule, which is how somebody set to fail is put back.
            return citizens.SetRule(id, Optional(form, "rule")) is null
                ? Results.NotFound()
                : See(http, $"{Root}/citizens");
        });

        // A form cannot send DELETE, so the verb is a POST and the path says what it does.
        app.MapPost($"{Root}/citizens/{{id}}/delete", (HttpContext http, Citizens citizens, string id) =>
            citizens.Remove(id) ? See(http, $"{Root}/citizens") : Results.NotFound());

        app.MapPost($"{Root}/behaviour", async (HttpContext http, EnqueuedDecisions queue) =>
        {
            var form = await http.Request.ReadFormAsync();
            var approve = form["outcome"].ToString() == "approve";
            var citizen = Optional(form, "citizen");

            var clientId = Optional(form, "clientId");

            if (!approve)
            {
                queue.Enqueue(
                    Decision.Refused(Optional(form, "errorCode") ?? "mitid_user_aborted"), clientId);

                return See(http, $"{Root}/behaviour");
            }

            // Refused rather than falling back to "default". The form's own picker always sends
            // somebody, so a request without one is hand-made - and naming a citizen who may since
            // have been deleted queues an approval that fails later for a reason nobody can trace.
            if (citizen is null)
            {
                return See(http, $"{Root}/behaviour", "citizen");
            }

            queue.Enqueue(Decision.Approved(citizen), clientId);

            return See(http, $"{Root}/behaviour");
        });

        app.MapPost($"{Root}/behaviour/clear", (HttpContext http, EnqueuedDecisions queue) =>
        {
            queue.Clear();

            return See(http, $"{Root}/behaviour");
        });
    }

    /// <summary>A form field, or null where a blank box means "not given".</summary>
    private static string? Optional(IFormCollection form, string field) =>
        form[field].ToString() is { Length: > 0 } value ? value : null;

    /// <summary>Post, redirect, get, for the pages that are not deciding a login.</summary>
    private static IResult See(HttpContext http, string path, string? problem = null)
    {
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = problem is null ? path : $"{path}?problem={problem}";

        return Results.Empty;
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

    /// <summary>
    /// The page: the rows, and the small script that keeps them current.
    /// </summary>
    /// <remarks>
    /// Everything here works with no JavaScript at all - the table is rendered by the server and
    /// the Refresh link reloads it. The script only replaces the refreshing a person would
    /// otherwise do by hand, which is why it can fail silently: an instance that has gone away
    /// leaves the last table on screen and the next tick tries again.
    /// </remarks>
    private static Html Logins(
        HttpContext http, SessionStore sessions, TimeProvider clock,
        string? state, string? clientId) => H($"""
        <p class="dim">
        <a href="{http.Request.Path + http.Request.QueryString}">Refresh</a>
        <span id="live" hidden>- updating every two seconds</span>
        </p>
        <div id="logins">{Rows(sessions, clock, state, clientId)}</div>
        <script>{new Html(Live)}</script>
        """);

    private const string Live = """

        (function () {
          var board = document.getElementById('logins');
          var last = null;

          document.getElementById('live').hidden = false;

          setInterval(function () {
            fetch('/_stubid/admin/rows' + location.search)
              .then(function (answer) { return answer.ok ? answer.text() : null; })
              .then(function (rows) {
                if (rows !== null && rows !== last) {
                  last = rows;
                  board.innerHTML = rows;
                }
              })
              .catch(function () { /* the instance went away; the next tick tries again */ });
          }, 2000);
        })();

        """;

    private static Html Rows(
        SessionStore sessions, TimeProvider clock, string? state, string? clientId)
    {
        // The same filter the control API answers to, and the same method: two copies of it would
        // be two things to keep agreeing.
        var matching = sessions.Matching(state, clientId);

        if (matching.Count == 0)
        {
            // Told apart, because they call for different things. Nothing here at all means start
            // a login; nothing matching means widen the filter, and saying "no logins yet" to
            // somebody looking at an instance full of them is how a page loses their trust.
            return sessions.Matching(null, null).Count > 0
                ? H($"""<p class="empty">No login matches that filter.</p>""")
                : H($"""
                    <p class="empty">No logins yet. Start one from your application and it appears
                    here.</p>
                    <p class="dim">A login parks and waits for a decision only when the instance
                    was started with <code>StubId__ApproveAutomatically=false</code>. Otherwise it
                    is decided before you can see it, and what you get here is the record.</p>
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

    /// <summary>
    /// The people this instance can sign in as.
    /// </summary>
    /// <remarks>
    /// The rule is editable and nothing else is. A personal number is derived at creation from the
    /// date of birth, so moving a birthday without it would leave somebody whose number disagrees
    /// with their own age, and the rest is identity a login reads rather than setup a person
    /// tunes. Delete and add again is the honest way to change those, and it is one click.
    /// </remarks>
    private static Html People(Citizens citizens, string? problem) => H($"""
        {Note(problem)}
        <table>
        <tr><th>Id</th><th>Name</th><th>Born</th><th>Personal number</th><th>Username</th>
        <th>Signing in as them</th><th></th></tr>
        {Join(citizens.All
            .OrderBy(citizen => citizen.Id, StringComparer.Ordinal)
            .Select(Person))}
        </table>

        <h2>Adding somebody</h2>
        <p class="dim">The personal number is generated and never supplied, and it is always a
        replacement number: the day of month is raised into the 61 to 91 range, which no issued
        number uses. A number this instance produces cannot belong to anybody.</p>
        <form method="post" action="{Root}/citizens">
        <p><label>Name <input name="name" size="30" required></label>
        <label>born <input name="dateOfBirth" placeholder="1985-03-29" size="12" required></label>
        <label>registered as
        <select name="gender"><option value="female">female</option>
        <option value="male">male</option></select></label></p>
        <p><label>Id <input name="id" placeholder="chosen for you" size="16"></label>
        <label>username <input name="userName" size="16"></label>
        <label>and signing in as them <input name="rule" placeholder="approves" size="22"></label></p>
        <p><button type="submit">Add</button></p>
        </form>
        """);

    private static Html Person(Citizen citizen) => H($"""
        <tr>
        <td><code>{citizen.Id}</code></td>
        <td>{citizen.Name}</td>
        <td class="dim">{citizen.DateOfBirth}</td>
        <td><code>{citizen.Cpr}</code></td>
        <td>{citizen.UserName ?? "-"}</td>
        <td>
        <form method="post" action="{Root}/citizens/{Uri.EscapeDataString(citizen.Id)}/rule" class="inline">
        <input name="rule" value="{citizen.Rule}" placeholder="approves" size="22">
        <button type="submit">Save</button>
        </form>
        </td>
        <td>
        <form method="post" action="{Root}/citizens/{Uri.EscapeDataString(citizen.Id)}/delete" class="inline">
        <button type="submit">Delete</button>
        </form>
        </td>
        </tr>
        """);

    /// <summary>
    /// What is waiting to be taken by the next login, which nothing could see until now.
    /// </summary>
    /// <remarks>
    /// Tier 2 is the tier suites use most, and a decision queued by one test and spent by the
    /// next is the hardest kind of surprise to explain from the outside. Reading the queue does
    /// not consume it.
    /// </remarks>
    private static Html Behaviours(EnqueuedDecisions queue, Citizens citizens, string? problem)
    {
        var queued = queue.Snapshot();

        var rows = queued.Count == 0
            ? H($"""<p class="empty">Nothing is queued. Every login is decided by the tiers below it.</p>""")
            : H($"""
                <table>
                <tr><th>For</th><th>Next</th><th>Does</th><th>As</th><th>Refusing with</th></tr>
                {Join(queued.SelectMany(entry => entry.Queued.Select((decision, index) => H($"""
                    <tr>
                    <td><code>{(entry.ClientId == "*" ? "any client" : Short(entry.ClientId))}</code></td>
                    <td class="dim">{index + 1}</td>
                    <td>{(decision.Approve ? "approves" : "refuses")}</td>
                    <td>{decision.CitizenId ?? "-"}</td>
                    <td><code>{decision.ErrorCode ?? "-"}</code></td>
                    </tr>
                    """))))}
                </table>
                <form method="post" action="{Root}/behaviour/clear">
                <p><button type="submit">Clear the queue</button></p>
                </form>
                """);

        var people = Join(citizens.All
            .OrderBy(citizen => citizen.Id, StringComparer.Ordinal)
            .Select(citizen => H($"""<option value="{citizen.Id}">{citizen.Name}</option>""")));

        return H($"""
            {Note(problem)}
            <p class="dim">A queued decision is taken by the next login that matches it, once, and
            is then gone. It is the one way to approve somebody whose own rule refuses.</p>
            {rows}

            <h2>Queueing one</h2>
            <form method="post" action="{Root}/behaviour">
            <p><label>For <input name="clientId" placeholder="any client" size="38"></label></p>
            <p><label><input type="radio" name="outcome" value="approve" checked> approve as
            <select name="citizen">{people}</select></label></p>
            <p><label><input type="radio" name="outcome" value="refuse"> refuse with
            <input name="errorCode" value="mitid_user_aborted" size="24"></label></p>
            <p><button type="submit">Queue it</button></p>
            </form>
            """);
    }

    // Codes rather than messages, so nothing a caller typed is reflected back into a page.
    private static Html Note(string? problem) => problem switch
    {
        "citizen" => Told("That citizen is not on this instance, so nothing was decided."),
        "name" => Told("A person needs a name."),
        "date" => Told("That date of birth could not be read. Write it as 1985-03-29."),
        _ => Html.Empty,
    };

    private static Html Told(string message) => H($"<p><strong>{message}</strong></p>");

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
