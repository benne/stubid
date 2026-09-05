using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using StubId.Server.Sessions;
using StubId.Wire;

namespace StubId.Server;

/// <summary>
/// StubID's own surface, which a test drives.
/// </summary>
/// <remarks>
/// Under a leading-underscore segment, which no emulated broker uses, so it can never collide
/// with a path StubID is reproducing.
/// </remarks>
public static class ControlApi
{
    public static void MapControlApi(this WebApplication app)
    {
        var api = app.MapGroup("/_stubid/v1");

        api.MapGet("/fidelity", () => Results.Json(new
        {
            entries = FidelityLedger.Read(typeof(Tokens).Assembly, typeof(JwsWriter).Assembly),
        }));

        // The three clients this broker publishes, which a reader currently finds by grepping the
        // source for a GUID. They are the emulated surface rather than state: read-only, and no
        // route registers a fourth, because the real broker's are fixed too.
        api.MapGet("/clients", (BrokerState state) => Results.Json(new
        {
            clients = state.Clients.Values
                .OrderBy(client => client.ClientId, StringComparer.Ordinal)
                .Select(client => new
                {
                    client.ClientId,
                    client.ResponseTypes,
                    client.Organisation,
                }),
        }));

        // What this build answers on, read from the routes it actually loaded rather than from a
        // list somebody maintains beside them. A profile that stopped declaring a route stops
        // showing one here, which a hand-written table would not.
        api.MapGet("/routes", (ProfileEndpointDataSource routes) => Results.Json(new
        {
            routes = routes.Endpoints
                .OfType<RouteEndpoint>()
                .Select(endpoint => new
                {
                    pattern = endpoint.RoutePattern.RawText,
                    methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                        ?? (IReadOnlyList<string>)[],
                    role = endpoint.Metadata.GetMetadata<RouteRules>()?.Role.Name,
                })
                .OrderBy(route => route.pattern, StringComparer.Ordinal),
        }));

        // Sessions
        api.MapGet("/sessions", (SessionStore sessions, string? state, string? clientId) =>
            Results.Json(sessions.Matching(state, clientId).Select(Describe)));

        api.MapGet("/sessions/{id}", (SessionStore sessions, string id) =>
            sessions.Find(id) is { } session ? Results.Json(Describe(session)) : Results.NotFound());

        // Why a login went the way it did, tier by tier, skipped ones included. Precedence is
        // unusable without it the moment more than one rule could apply.
        api.MapGet("/sessions/{id}/explain", (SessionStore sessions, string id) =>
            sessions.Find(id) is { } session
                ? Results.Json(new
                {
                    session = session.Id,
                    outcome = session.State.ToString(),
                    ladder = session.Explanation.Select(s => new
                    {
                        tier = s.Tier == int.MaxValue ? null : (int?)s.Tier,
                        s.Name,
                        s.Outcome,
                        s.Reason,
                    }),
                })
                : Results.NotFound());

        api.MapPost("/sessions/{id}/approve", (
            SessionStore sessions, Citizens citizens, string id, ApproveRequest? body) =>
        {
            var outcome = Approvals.Approve(sessions, citizens, id, body?.CitizenId, "the control API");

            return outcome.Result switch
            {
                ApprovalResult.NoSuchCitizen => Results.BadRequest(new { error = "no such citizen" }),
                ApprovalResult.Decided => Results.Json(new
                {
                    decided = true,
                    citizen = outcome.CitizenId,
                    state = outcome.Session?.State.ToString(),
                }),

                // 409 rather than an error: the caller lost a race, and what it needs is the
                // outcome that actually happened.
                ApprovalResult.AlreadyDecided => Conflict(outcome.Session!),
                _ => Results.NotFound(),
            };
        });

        api.MapPost("/sessions/{id}/reject", (SessionStore sessions, string id, RejectRequest? body) =>
        {
            var outcome = Approvals.Reject(sessions, id, body?.ErrorCode, body?.Error, "the control API");

            return outcome.Result switch
            {
                ApprovalResult.Decided => Results.Json(new { decided = true }),
                ApprovalResult.AlreadyDecided => Conflict(outcome.Session!),
                _ => Results.NotFound(),
            };
        });

        // Behaviour
        api.MapPost("/behaviours/enqueue", (EnqueuedDecisions queue, EnqueueRequest body) =>
        {
            queue.Enqueue(
                body.Approve
                    ? Decision.Approved(body.CitizenId ?? "default")
                    : Decision.Refused(body.ErrorCode ?? "mitid_user_aborted", body.Error ?? "access_denied"),
                body.ClientId);

            return Results.Accepted();
        });

        // Citizens
        api.MapGet("/citizens", (Citizens citizens) => Results.Json(citizens.All));

        api.MapPost("/citizens", (Citizens citizens, CreateCitizenRequest body) =>
        {
            var born = DateOnly.Parse(body.DateOfBirth, System.Globalization.CultureInfo.InvariantCulture);
            var citizen = citizens.Create(
                body.Id, body.Name, born,
                string.Equals(body.Gender, "male", StringComparison.OrdinalIgnoreCase)
                    ? Gender.Male
                    : Gender.Female,
                body.UserName,
                body.Rule);

            return Results.Created($"/_stubid/v1/citizens/{citizen.Id}", citizen);
        });

        // The route POST /citizens has been handing out in a Location header since it was
        // written, and which did not exist until now.
        api.MapGet("/citizens/{id}", (Citizens citizens, string id) =>
            citizens.ById(id) is { } citizen ? Results.Json(citizen) : Results.NotFound());

        // The one field on a person it is safe to change, and the only one worth changing while
        // something is running: whether signing in as them works. Sending no rule clears it,
        // which is how a person set to fail is put back.
        api.MapPatch("/citizens/{id}", (Citizens citizens, string id, SetRuleRequest? body) =>
            citizens.SetRule(id, body?.Rule) is { } citizen
                ? Results.Json(citizen)
                : Results.NotFound());

        api.MapDelete("/citizens/{id}", (Citizens citizens, string id) =>
            citizens.Remove(id) ? Results.NoContent() : Results.NotFound());

        // What is queued, in the order it will be taken, without taking any of it. Tier 2 is the
        // tier suites use most and was the only one nothing could look at, which made a decision
        // left over from an earlier test the hardest kind of surprise to explain.
        api.MapGet("/behaviours", (EnqueuedDecisions queue) => Results.Json(new
        {
            queued = queue.Snapshot().SelectMany(entry => entry.Queued.Select((decision, index) => new
            {
                clientId = entry.ClientId,
                position = index + 1,
                decision.Approve,
                decision.CitizenId,
                decision.ErrorCode,
                error = decision.OAuthError,
            })),
        }));

        api.MapDelete("/behaviours", (EnqueuedDecisions queue) =>
        {
            queue.Clear();

            return Results.NoContent();
        });

        // What this instance has handed out, and never what it handed out. The value of a code or
        // an access token is the credential itself, so nothing here reads one: an entry lines up
        // against a login by its session id, which is public already.
        api.MapGet("/issued", (BrokerState state) => Results.Json(new { issued = state.Issued() }));

        // Time.
        //
        // Reading it is always allowed, and every argument about a timeout starts by asking what
        // the instance thinks the time is. It also lets a page say how long a login has left
        // without keeping a clock of its own and disagreeing with the one that decides.
        api.MapGet("/time", (TimeProvider clock) => Results.Json(new
        {
            now = clock.GetUtcNow(),
            controllable = clock is FakeTimeProvider,
        }));

        // Moving it is not. Only where the clock is controllable, which is how a five-minute
        // timeout is exercised in milliseconds rather than waited out.
        api.MapPost("/time/advance", (TimeProvider clock, AdvanceRequest body) =>
        {
            if (clock is not FakeTimeProvider controllable)
            {
                return Results.BadRequest(new
                {
                    error = "the clock is not controllable",
                    detail = "Start with StubId:ControllableClock=true to move time.",
                });
            }

            controllable.Advance(TimeSpan.FromSeconds(body.Seconds));
            return Results.Json(new { now = controllable.GetUtcNow() });
        });

        // Protocol state, not setup: sessions and anything still queued go, and the citizens
        // a suite created stay, so a fixture built once survives the reset between tests.
        api.MapPost("/reset", (
            SessionStore sessions, EnqueuedDecisions queue, BrokerState state, CprMatch attempts) =>
        {
            sessions.Clear();
            queue.Clear();

            // Added later than the two above, and it is a change rather than a completion: a
            // reset used to leave codes, access tokens and pushed requests standing, so one taken
            // before it could still be redeemed after. Nothing here is setup a suite builds once,
            // which is the line this endpoint draws - and which is why the citizens still stay.
            state.Forget();
            attempts.Clear();

            return Results.NoContent();
        });

        // Where the address is settable after the process has started. A container does not learn
        // its own mapped host port until Docker has started it, and the caller that mapped it is
        // the only party that knows - so a test module starts the instance, reads the port, and
        // tells it, before anything has discovered a document with the wrong issuer in it.
        // Whether logins decide themselves, switchable while the instance runs. A container
        // restart to change one boolean would take the sessions somebody was watching with it.
        api.MapGet("/runtime/automatic-approval", (AutomaticApproval approval) => Results.Json(new
        {
            enabled = approval.Enabled,
            configured = approval.Configured,
            overridden = approval.Overridden,
        }));

        api.MapPut("/runtime/automatic-approval", (
            AutomaticApproval approval, AutomaticApprovalRequest? body) =>
        {
            // A null enabled clears the override rather than meaning false, so an instance can be
            // put back to what it was started with.
            approval.Set(body?.Enabled);

            return Results.Json(new
            {
                enabled = approval.Enabled,
                configured = approval.Configured,
                overridden = approval.Overridden,
            });
        });

        api.MapGet("/runtime/public-base-url", (PublicBaseUrl publicBaseUrl) =>
            Results.Json(new { publicBaseUrl = publicBaseUrl.Value }));

        api.MapPut("/runtime/public-base-url",
            (PublicBaseUrl publicBaseUrl, PublicBaseUrlRequest? body) =>
        {
            if (!PublicBaseUrl.TryNormalise(body?.PublicBaseUrl, out var normalised, out var fault))
            {
                return Results.BadRequest(new { error = fault.Error, detail = fault.Detail });
            }

            publicBaseUrl.Set(normalised);

            return Results.Json(new { publicBaseUrl = normalised });
        });

        // The public half of the certificate this instance serves TLS with, so a caller can trust
        // exactly this instance and nothing else. Served over whichever transport asked, which is
        // the point: fetching it over plain HTTP is how a client learns what to expect on the
        // secured one without a trust decision it has not been given the means to make yet.
        //
        // The private key is not here and has no route that would reach it.
        api.MapGet("/runtime/tls-certificate", (IServiceProvider services) =>
        {
            if (services.GetService<ServerCertificate>() is not { } tls)
            {
                return Results.Json(new { certificate = (string?)null, thumbprint = (string?)null });
            }

            return Results.Json(new
            {
                certificate = Convert.ToBase64String(
                    tls.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert)),
                thumbprint = tls.Certificate.Thumbprint,
                notAfter = tls.Certificate.NotAfter,
            });
        });

        // The same certificate as the route above, in the encoding everything that is not .NET
        // reads. One curl puts it in a file, and curl --cacert, NODE_EXTRA_CA_CERTS and keytool all
        // take it from there; base64 DER inside a JSON body is consumable by a .NET client and by
        // nothing else.
        //
        // The trailing newline is not cosmetic. A caller trusting a second instance appends to this
        // file, and without one the join reads "-----END CERTIFICATE----------BEGIN CERTIFICATE-----",
        // which no parser accepts. Written literally rather than through Environment.NewLine: what
        // goes on the wire must not depend on the operating system StubID happens to run on.
        api.MapGet("/runtime/tls-certificate.pem", (IServiceProvider services) =>
            services.GetService<ServerCertificate>() is { } tls
                ? Results.Text(
                    tls.Certificate.ExportCertificatePem() + "\n",
                    "application/pem-certificate-chain")

                // 404 rather than an empty 200, because this route is the certificate rather than a
                // question about one. A success that writes an empty file is discovered later, as a
                // handshake failure with nothing on the caller's side to explain it.
                : Results.Json(
                    new
                    {
                        error = "this instance serves plain HTTP",
                        detail = "Start it with StubId:Tls=self-signed to serve TLS, or with "
                            + "StubId:Tls=pkcs12 and a certificate of your own.",
                    },
                    statusCode: StatusCodes.Status404NotFound));

        app.MapGet("/_stubid/health/live", () => Results.Ok());

        // Live is "the process answers"; ready is "the process can answer correctly". The split is
        // what makes the handshake above work: a caller polls live to know it may set the address,
        // and waits on ready to know the setting landed. There is no shell in the runtime image,
        // so an HTTP answer is the only readiness signal anything outside can use.
        app.MapGet("/_stubid/health/ready", (PublicBaseUrl publicBaseUrl) =>
            publicBaseUrl.IsSet
                ? Results.Ok()
                : Results.Json(
                    new
                    {
                        error = "the public base URL is not set",
                        detail = PublicBaseUrl.NotSetDetail,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    private static IResult Conflict(AuthSession session) => Results.Json(new
    {
        decided = false,
        detail = "something already decided this login",
        outcome = Describe(session),
    }, statusCode: StatusCodes.Status409Conflict);

    /// <remarks>
    /// The transaction text is deliberately absent. <see cref="AuthSession.TransactionText" />
    /// says why: keeping the decoded string on a long-lived session would put a client-controlled
    /// string into everything that describes one, and the decode costs nothing to repeat where it
    /// is rendered.
    /// </remarks>
    private static object Describe(AuthSession session) => new
    {
        session.Id,
        session.ClientId,
        state = session.State.ToString(),
        session.CitizenId,
        session.ErrorCode,

        // The other half of the pair a refusal sends. Refuse() puts ErrorCode in
        // error_description and this in error, and only one of them was readable.
        oauthError = session.OAuthError,
        session.CreatedAt,
        session.Deadline,
        session.DecidedAt,

        // The token the transition is guarded by, so a caller can tell one read from the next.
        session.Version,
    };

    public sealed record ApproveRequest(string? CitizenId);

    public sealed record RejectRequest(string? ErrorCode, string? Error);

    public sealed record EnqueueRequest(
        bool Approve, string? ClientId, string? CitizenId, string? ErrorCode, string? Error);

    public sealed record CreateCitizenRequest(
        string Name, string DateOfBirth, string? Gender, string? Id, string? UserName, string? Rule);

    public sealed record AdvanceRequest(double Seconds);

    /// <remarks>
    /// A null rule is a cleared rule rather than an absent field. There is one field here, so
    /// there is nothing a caller could mean by omitting it except "no rule".
    /// </remarks>
    public sealed record SetRuleRequest(string? Rule);

    /// <remarks>
    /// Nullable throughout: no body, or a null <c>enabled</c>, puts the instance back to the
    /// setting it was started with rather than turning approval off.
    /// </remarks>
    public sealed record AutomaticApprovalRequest(bool? Enabled);

    /// <remarks>
    /// Nullable so a missing body is refused with our own message rather than the framework's
    /// empty one - the caller who sends no body is the caller who most needs telling what to send.
    /// </remarks>
    public sealed record PublicBaseUrlRequest(string? PublicBaseUrl);
}
