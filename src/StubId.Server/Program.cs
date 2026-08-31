using StubId.Profiles;
using StubId.Server;
using StubId.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();
// A controllable clock starts where the real one is, so timestamps in tokens still look
// like the moment the instance started, and reads it through TimeProvider rather than the
// ambient DateTimeOffset - which is the rule the rest of the codebase is held to.
builder.Services.AddSingleton<TimeProvider>(_ =>
    builder.Configuration.GetValue("StubId:ControllableClock", defaultValue: false)
        ? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TimeProvider.System.GetUtcNow())
        : TimeProvider.System);
builder.Services.AddSingleton<Documents>();
builder.Services.AddSingleton<Keys>();
builder.Services.AddSingleton<PublicBaseUrl>();
builder.Services.AddSingleton<BrokerState>();
builder.Services.AddSingleton<CprMatch>();

// The approval engine. Automatic by default, because a test that hangs waiting for a person
// is worse than one that does not exercise the waiting; set StubId:ApproveAutomatically to
// false for an instance somebody is watching.
builder.Services.AddSingleton<Citizens>();
builder.Services.AddSingleton<EnqueuedDecisions>();
builder.Services.AddSingleton(sp => new Ladder(
[
    sp.GetRequiredService<EnqueuedDecisions>(),
    new SimulationParameter(sp.GetRequiredService<Citizens>()),
    new CitizenRules(sp.GetRequiredService<Citizens>()),
    new DefaultOutcome(
        sp.GetRequiredService<Citizens>(),
        () => sp.GetRequiredService<IConfiguration>()
                .GetValue("StubId:ApproveAutomatically", defaultValue: true)),
]));
builder.Services.AddSingleton(sp => new SessionStore(
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Ladder>()));
builder.Services.AddSingleton<Tokens>();

builder.Services.AddSingleton(new PathRules("/op"));
builder.Services.AddSingleton<ProfileEndpointDataSource>();
builder.Services.AddSingleton<IBrokerProfile, NetsEidBrokerProfile>();

var app = builder.Build();

// An instance that has not been told its own address says so, in the same shape the clock
// refuses in. The alternative is a plausible wrong issuer, which every client accepts when it is
// configured and rejects later with nothing on its side to explain why.
app.Use(async (http, next) =>
{
    try
    {
        await next();
    }
    catch (PublicBaseUrlNotSetException) when (!http.Response.HasStarted)
    {
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        await http.Response.WriteAsJsonAsync(new
        {
            error = "the public base URL is not set",
            detail = PublicBaseUrl.NotSetDetail,
        });
    }
});

// Before routing, because the framework would already have matched a path the broker refuses.
app.Use(async (http, next) =>
{
    var rules = http.RequestServices.GetRequiredService<PathRules>();

    if (http.Request.Path.StartsWithSegments("/_stubid") || rules.Accepts(http.Request.Path))
    {
        await next();
        return;
    }

    http.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.MapControlApi();

// The broker's own routes come from the profile, not from a fixed table. Loading them runs the
// collision scan, which refuses to start rather than throwing on every request later.
var profile = app.Services.GetRequiredService<IBrokerProfile>();
var publicBaseUrl = app.Services.GetRequiredService<PublicBaseUrl>();
var routes = app.Services.GetRequiredService<ProfileEndpointDataSource>();

// A snapshot of the address as the routes were loaded, and not a second source of truth: the
// value moves at runtime and a route table does not get rebuilt when it does. No profile reads
// it today, and one that needs it must read PublicBaseUrl per request instead.
var seeded = publicBaseUrl.Value ?? "";

routes.Load([(profile, new ProfileContext($"{seeded}/op", seeded), MountPrefix: "")]);
((IEndpointRouteBuilder)app).DataSources.Add(routes);

app.Run();

/// <summary>Named so the tests can host this application.</summary>
public partial class Program;
