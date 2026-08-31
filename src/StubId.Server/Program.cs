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
builder.Services.AddSingleton<BrokerState>();

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
var issuer = $"{builder.Configuration["StubId:PublicBaseUrl"] ?? "http://localhost"}/op";
var routes = app.Services.GetRequiredService<ProfileEndpointDataSource>();

routes.Load([(profile, new ProfileContext(issuer, issuer[..^3]), MountPrefix: "")]);
((IEndpointRouteBuilder)app).DataSources.Add(routes);

app.Run();

/// <summary>Named so the tests can host this application.</summary>
public partial class Program;
