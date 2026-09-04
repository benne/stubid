using System.Net;
using System.Net.Http.Json;

namespace StubId.Client;

/// <summary>
/// StubID's control API, typed. What a test drives an instance with.
/// </summary>
/// <remarks>
/// Refusals come back three ways, and which way is deliberate. A query for something that is not
/// there returns absence, so a missing session reads as null rather than as a failure. A decision
/// that lost its race returns the outcome that won, because "the tester clicked approve as the
/// timeout fired" is an ordinary event in a suite that exercises timeouts and both writers should
/// learn the same answer. Everything else - a citizen that does not exist, a clock that was never
/// made controllable - throws <see cref="StubIdException" />, because the caller has something to
/// change.
/// </remarks>
public sealed class StubIdClient : IDisposable
{
    private readonly bool _ownsHttp;

    /// <summary>
    /// Over a client the caller owns, such as one from a test host or an
    /// <c>IHttpClientFactory</c>. Its base address must be the instance root, not the control API.
    /// </summary>
    public StubIdClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);

        Http = http;
        _ownsHttp = false;
        Citizens = new CitizenApi(http);
        Sessions = new SessionApi(http);
        Behaviour = new BehaviourApi(http);
        Time = new ClockApi(http);
        Runtime = new RuntimeApi(http);
    }

    /// <summary>Over an address, with a client of its own that this instance disposes.</summary>
    public StubIdClient(Uri baseAddress)
        : this(new HttpClient { BaseAddress = baseAddress }) => _ownsHttp = true;

    /// <summary>The transport, for anything this surface does not cover yet.</summary>
    public HttpClient Http { get; }

    public CitizenApi Citizens { get; }

    public SessionApi Sessions { get; }

    public BehaviourApi Behaviour { get; }

    public ClockApi Time { get; }

    public RuntimeApi Runtime { get; }

    /// <summary>
    /// Clears the sessions and anything queued. Citizens survive, so a suite builds its people
    /// once.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        using var response = await Http.PostAsync("/_stubid/v1/reset", content: null, ct);

        await Control.EnsureAsync(response, ct);
    }

    /// <summary>Every divergence this instance admits to, read from the code that emits it.</summary>
    public async Task<IReadOnlyList<FidelityEntry>> FidelityAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync("/_stubid/v1/fidelity", ct);

        await Control.EnsureAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync(ControlJson.Default.EntriesBody, ct);

        return body?.Entries ?? [];
    }

    /// <summary>
    /// The clients this broker publishes, which cannot be added to.
    /// </summary>
    /// <remarks>
    /// Read rather than written down: a suite that pins one of these in a constant has pinned a
    /// GUID it found by grepping, and finding out it moved is a failure at the authorize hop
    /// rather than here.
    /// </remarks>
    public async Task<IReadOnlyList<RegisteredClient>> ClientsAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync("/_stubid/v1/clients", ct);
        var body = await Control.ReadAsync(response, ControlJson.Default.ClientsBody, ct);

        return body.Clients;
    }

    /// <summary>
    /// Every route this build answers on, read from the ones it actually loaded.
    /// </summary>
    /// <remarks>
    /// The profile declares them and the engine builds them, so this is what the instance will
    /// really match rather than a list maintained beside it. A route a profile stopped declaring
    /// stops appearing here, which a hand-written table would not.
    /// </remarks>
    public async Task<IReadOnlyList<EmulatedRoute>> RoutesAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync("/_stubid/v1/routes", ct);
        var body = await Control.ReadAsync(response, ControlJson.Default.RoutesBody, ct);

        return body.Routes;
    }

    /// <summary>The process answers.</summary>
    /// <remarks>Never throws: this is a poll predicate, and one that throws is unusable.</remarks>
    public Task<bool> IsLiveAsync(CancellationToken ct = default) =>
        AnswersAsync("/_stubid/health/live", ct);

    /// <summary>
    /// The process can answer correctly - it has been told its own address. False until something
    /// sets one.
    /// </summary>
    /// <remarks>Never throws, for the same reason as <see cref="IsLiveAsync" />.</remarks>
    public Task<bool> IsReadyAsync(CancellationToken ct = default) =>
        AnswersAsync("/_stubid/health/ready", ct);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            Http.Dispose();
        }
    }

    private async Task<bool> AnswersAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(path, ct);

            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // A timeout on the way up, not a caller who changed their mind.
            return false;
        }
    }
}
