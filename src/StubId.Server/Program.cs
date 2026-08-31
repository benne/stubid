using StubId.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStubId(builder.Configuration);

// TLS is off unless asked for, and adds a listener rather than replacing one. The issuer is stored
// data, so both listeners render the same URLs and there is still exactly one issuer; what that buys
// is a control API reachable without trusting anything, which is what lets a test module create a
// citizen before it has seen the certificate.
//
// Loading the certificate and binding the listener that serves it are one decision, which is why
// this stays here rather than moving into the shared composition: a host with no listener has
// nothing to do with either half.
var serverCertificate = ServerCertificate.Load(builder.Configuration);

if (serverCertificate is not null)
{
    builder.Services.AddSingleton(serverCertificate);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(8080);
        kestrel.ListenAnyIP(8443, listener => listener.UseHttps(serverCertificate.Certificate));
    });
}

var app = builder.Build();

app.UseStubId();

app.Run();

/// <summary>Named so the tests can host this application.</summary>
public partial class Program;
