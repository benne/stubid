using StubId.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Documents>();
builder.Services.AddSingleton<Keys>();
builder.Services.AddSingleton<BrokerState>();
builder.Services.AddSingleton<Tokens>();

builder.Services.AddSingleton(new PathRules("/op"));

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

app.MapBroker();
app.Run();

/// <summary>Named so the tests can host this application.</summary>
public partial class Program;
