using StubId.Profiles;
using StubId.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Documents>();
builder.Services.AddSingleton<Keys>();
builder.Services.AddSingleton<BrokerState>();
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

// StubID's own surface, which no emulated broker can collide with: none uses a leading
// underscore segment.
app.MapGet("/_stubid/v1/fidelity", () => Results.Json(new
{
    entries = FidelityLedger.Read(typeof(Tokens).Assembly, typeof(StubId.Wire.JwsWriter).Assembly),
}));

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
