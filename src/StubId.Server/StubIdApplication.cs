using StubId.Abstractions;
using StubId.Profiles;
using StubId.Server.Sessions;

namespace StubId.Server;

/// <summary>
/// Everything StubID is, composed onto a host somebody else built.
/// </summary>
/// <remarks>
/// The entry point used to hold this inline, which meant the only way to run the emulator was to
/// run its executable. A test process that hosts it in memory needs the same composition on a
/// host of its own, and the two must be the same composition rather than two that resemble each
/// other - an emulator whose hosting modes disagree about middleware order is the class of bug
/// this project exists to catch.
/// <para>
/// What listens is deliberately not here. That is the entry point's business, and the one host
/// that listens is the only one with a reason to know about ports.
/// </para>
/// </remarks>
public static class StubIdApplication
{
    /// <summary>Registers the emulator's services. Reads configuration, resolves nothing.</summary>
    public static IServiceCollection AddStubId(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDataProtection();

        // A controllable clock starts where the real one is, so timestamps in tokens still look
        // like the moment the instance started, and reads it through TimeProvider rather than the
        // ambient DateTimeOffset - which is the rule the rest of the codebase is held to.
        services.AddSingleton<TimeProvider>(_ =>
            configuration.GetValue("StubId:ControllableClock", defaultValue: false)
                ? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TimeProvider.System.GetUtcNow())
                : TimeProvider.System);
        services.AddSingleton<Documents>();
        services.AddSingleton<Keys>();
        services.AddSingleton<PublicBaseUrl>();
        services.AddSingleton<BrokerState>();
        services.AddSingleton<CprMatch>();

        // The approval engine. Automatic by default, because a test that hangs waiting for a person
        // is worse than one that does not exercise the waiting; set StubId:ApproveAutomatically to
        // false for an instance somebody is watching.
        services.AddSingleton<Citizens>();
        services.AddSingleton<EnqueuedDecisions>();
        services.AddSingleton(sp => new Ladder(
        [
            sp.GetRequiredService<EnqueuedDecisions>(),
            new SimulationParameter(sp.GetRequiredService<Citizens>()),
            new CitizenRules(sp.GetRequiredService<Citizens>()),
            new DefaultOutcome(
                sp.GetRequiredService<Citizens>(),
                // Read per decision rather than captured, so an instance that is reconfigured
                // while it runs answers with the setting it has now.
                () => sp.GetRequiredService<IConfiguration>()
                        .GetValue("StubId:ApproveAutomatically", defaultValue: true)),
        ]));
        services.AddSingleton(sp => new SessionStore(
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Ladder>()));
        services.AddSingleton<Tokens>();

        services.AddSingleton(new PathRules("/op"));
        services.AddSingleton<ProfileEndpointDataSource>();
        services.AddSingleton<IBrokerProfile, NetsEidBrokerProfile>();

        return services;
    }

    /// <summary>The middleware and the routes, in the order the emulator needs them.</summary>
    public static WebApplication UseStubId(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // First, so that it is on the answers the rest of this pipeline gives without asking
        // anyone: the path gate below refuses by setting a status and returning, and a header
        // added after it would miss every 404.
        app.Use(AnnounceTheEmulator);

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

        return app;
    }

    /// <summary>
    /// Says on every response that this is an emulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one header StubID adds that the broker does not send, and it is added on purpose:
    /// everything else here exists to be indistinguishable from the real thing, which is
    /// precisely why an instance has to be able to say what it is. TRADEMARKS.md states this as
    /// an undertaking rather than a feature.
    /// </para>
    /// <para>
    /// Set before the request is handled rather than as the response is written, because a
    /// short-circuiting middleware never gets to a callback. The one path this does not survive
    /// is an exception escaping to the server, which resets the response and everything on it;
    /// no placement survives that.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.Divergent,
        Reason = "docs/brokers/neb/divergences.md#emulator-header")]
    private static Task AnnounceTheEmulator(HttpContext http, RequestDelegate next)
    {
        http.Response.Headers["X-StubID-Emulator"] = "1";

        return next(http);
    }
}
