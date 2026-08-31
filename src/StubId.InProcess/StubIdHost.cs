using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StubId.Client;
using StubId.Server;

namespace StubId.InProcess;

/// <summary>A StubID instance in this process, and the control API over it.</summary>
/// <remarks>
/// The same composition the container runs, on an in-memory transport instead of a socket. That
/// buys the two things a container cannot: a login costs no network at all, and the instance is in
/// the debugger with the test that drives it.
/// <para>
/// What it costs is that nothing outside this process can reach it. A browser, the Node and Spring
/// suites, or an application under test that is not .NET all need something to dial, which is what
/// StubId.Testing is for.
/// </para>
/// </remarks>
public sealed class StubIdHost : IAsyncDisposable
{
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<ILoggingBuilder>? _logging;

    private WebApplication? _app;
    private TestServer? _server;
    private HttpClient? _http;
    private StubIdClient? _control;

    internal StubIdHost(Dictionary<string, string?> settings, Action<ILoggingBuilder>? logging)
    {
        _settings = settings;
        _logging = logging;
        BaseAddress = new Uri(settings["StubId:PublicBaseUrl"]!);
    }

    /// <summary>The address this instance says it answers at, known before it is started.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// What a client library is configured with. The issuer it then discovers equals this
    /// character for character, which is the comparison openid-client and Spring Security both
    /// make and neither forgives.
    /// </summary>
    /// <remarks>Not <see cref="Uri.Authority" />, which is a host and a port.</remarks>
    public Uri Authority => new(BaseAddress, "op");

    /// <summary>The control API, over this instance.</summary>
    public StubIdClient Control => _control ?? throw NotStarted();

    public CitizenApi Citizens => Control.Citizens;

    public SessionApi Sessions => Control.Sessions;

    public BehaviourApi Behaviour => Control.Behaviour;

    public ClockApi Time => Control.Time;

    /// <summary>
    /// The instance's own services, for what only being in the process can do.
    /// </summary>
    /// <remarks>
    /// The control API is the supported surface and the one the container shares; this is the
    /// escape hatch for reaching a collaborator directly, which no containerised suite can. Code
    /// written against it does not move to a container unchanged, which is the trade.
    /// </remarks>
    public IServiceProvider Services => _app?.Services ?? throw NotStarted();

    /// <summary>Builds and starts the instance.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("This host is already started.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Nothing of the caller's is read as configuration: not the arguments their test
            // runner was given, not an appsettings.json that happens to sit beside their tests,
            // and not a content root that has to be guessed at from an assembly location.
            Args = [],
            ApplicationName = typeof(StubIdApplication).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // Last, so it beats the environment variables the default sources bring with them.
        builder.Configuration.AddInMemoryCollection(_settings);

        builder.Logging.ClearProviders();
        _logging?.Invoke(builder.Logging);

        // The address is set on the server as well as in configuration, so a client this host
        // hands out sends absolute URLs that match the issuer, and a redirect the instance emits
        // can be followed exactly as it was written.
        builder.WebHost.UseTestServer(options => options.BaseAddress = BaseAddress);

        builder.Services.AddStubId(builder.Configuration);

        var app = builder.Build();
        app.UseStubId();

        await app.StartAsync(ct);

        _app = app;

        // Resolved here rather than on the first request, so the key load - and, the first time
        // any instance runs on a machine, generating the keys - is paid by start rather than
        // charged to whatever a test happens to be timing.
        _ = app.Services.GetRequiredService<Keys>();

        _server = app.GetTestServer();
        _http = _server.CreateClient();
        _control = new StubIdClient(_http);
    }

    /// <summary>
    /// A handler that reaches this instance in memory. What a client library's back channel is
    /// pointed at.
    /// </summary>
    /// <remarks>
    /// The twin of the container module's trusting handler, and simpler for the reason that makes
    /// it simpler: there is no transport, so there is nothing to trust and no certificate to
    /// compare. Validation is not being waved through here - there is nothing to validate.
    /// </remarks>
    public HttpMessageHandler CreateHandler() => (_server ?? throw NotStarted()).CreateHandler();

    /// <summary>A client for driving the protocol by hand, addressed at this instance.</summary>
    /// <remarks>
    /// The caller owns it and disposes it. Unlike a client from a web application factory, this
    /// one does not follow redirects at all - redirect following belongs to the handler a real
    /// transport uses - so a test playing the browser needs to turn nothing off.
    /// </remarks>
    public HttpClient CreateClient() => (_server ?? throw NotStarted()).CreateClient();

    /// <summary>
    /// Clears the sessions and anything queued, and keeps the citizens. What a suite calls between
    /// tests when it reuses one instance.
    /// </summary>
    public Task ResetAsync(CancellationToken ct = default) => Control.ResetAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _control?.Dispose();
        _control = null;

        // The client is ours rather than the control client's: StubIdClient only disposes one it
        // opened itself.
        _http?.Dispose();
        _http = null;

        _server = null;

        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static InvalidOperationException NotStarted() =>
        new("This host is not started. Call StartAsync() first.");
}
