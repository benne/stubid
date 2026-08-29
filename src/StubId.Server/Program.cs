using StubId.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Documents>();
builder.Services.AddSingleton<Keys>();
builder.Services.AddSingleton<BrokerState>();
builder.Services.AddSingleton<Tokens>();

var app = builder.Build();
app.MapBroker();
app.Run();

/// <summary>Named so the tests can host this application.</summary>
public partial class Program;
