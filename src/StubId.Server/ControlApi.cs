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

        // Sessions
        api.MapGet("/sessions", (SessionStore sessions, string? state, string? clientId) =>
            Results.Json(sessions.All
                .Where(s => state is null || s.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase))
                .Where(s => clientId is null || s.ClientId == clientId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(Describe)));

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
            var citizen = body?.CitizenId is { } named ? citizens.ById(named) : citizens.Default;

            if (citizen is null)
            {
                return Results.BadRequest(new { error = "no such citizen" });
            }

            // The citizen's own rule applies here too. "Sign in as this person" has to mean
            // the same thing whether a test said it or someone clicked it.
            return sessions.Decide(id, citizen.Outcome(), "the control API")
                ? Results.Json(new
                {
                    decided = true,
                    citizen = citizen.Id,
                    state = sessions.Find(id)?.State.ToString(),
                })

                // 409 rather than an error: the caller lost a race, and what it needs is the
                // outcome that actually happened.
                : Conflict(sessions, id);
        });

        api.MapPost("/sessions/{id}/reject", (SessionStore sessions, string id, RejectRequest? body) =>
            sessions.Decide(
                id,
                Decision.Refused(body?.ErrorCode ?? "mitid_user_aborted", body?.Error ?? "access_denied"),
                "the control API")
                ? Results.Json(new { decided = true })
                : Conflict(sessions, id));

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

        api.MapDelete("/citizens/{id}", (Citizens citizens, string id) =>
            citizens.Remove(id) ? Results.NoContent() : Results.NotFound());

        // Time. Only where the clock is controllable, which is how a five-minute timeout is
        // exercised in milliseconds rather than waited out.
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
        api.MapPost("/reset", (SessionStore sessions, EnqueuedDecisions queue) =>
        {
            sessions.Clear();
            queue.Clear();

            return Results.NoContent();
        });

        app.MapGet("/_stubid/health/live", () => Results.Ok());
        app.MapGet("/_stubid/health/ready", () => Results.Ok());
    }

    private static IResult Conflict(SessionStore sessions, string id) =>
        sessions.Find(id) is { } session
            ? Results.Json(new
            {
                decided = false,
                detail = "something already decided this login",
                outcome = Describe(session),
            }, statusCode: StatusCodes.Status409Conflict)
            : Results.NotFound();

    private static object Describe(AuthSession session) => new
    {
        session.Id,
        session.ClientId,
        state = session.State.ToString(),
        session.CitizenId,
        session.ErrorCode,
        session.CreatedAt,
        session.Deadline,
        session.DecidedAt,
    };

    public sealed record ApproveRequest(string? CitizenId);

    public sealed record RejectRequest(string? ErrorCode, string? Error);

    public sealed record EnqueueRequest(
        bool Approve, string? ClientId, string? CitizenId, string? ErrorCode, string? Error);

    public sealed record CreateCitizenRequest(
        string Name, string DateOfBirth, string? Gender, string? Id, string? UserName, string? Rule);

    public sealed record AdvanceRequest(double Seconds);
}
