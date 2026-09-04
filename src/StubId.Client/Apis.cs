using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;

namespace StubId.Client;

/// <summary>The people a login can resolve as.</summary>
public sealed class CitizenApi(HttpClient http)
{
    public async Task<IReadOnlyList<StubIdCitizen>> ListAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/_stubid/v1/citizens", ct);

        return await Control.ReadAsync(response, ControlJson.Default.IReadOnlyListStubIdCitizen, ct);
    }

    /// <summary>
    /// Creates one, with a generated personal number that cannot belong to anybody.
    /// </summary>
    /// <remarks>
    /// The created citizen comes from the response body rather than from the Location header,
    /// which saves a round trip. The header names <see cref="FindAsync" />'s route, which it did
    /// not until that route was written.
    /// </remarks>
    public async Task<StubIdCitizen> CreateAsync(CitizenSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var body = new CreateCitizenBody(
            spec.Name,
            spec.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            spec.Gender?.ToString(),
            spec.Id,
            spec.UserName,
            spec.Rule);

        using var response = await http.PostAsJsonAsync(
            "/_stubid/v1/citizens", body, ControlJson.Default.CreateCitizenBody, ct);

        return await Control.ReadAsync(response, ControlJson.Default.StubIdCitizen, ct);
    }

    /// <summary>One person, or null if there is nobody by that name.</summary>
    public async Task<StubIdCitizen?> FindAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/_stubid/v1/citizens/{Uri.EscapeDataString(id)}", ct);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await Control.ReadAsync(response, ControlJson.Default.StubIdCitizen, ct);
    }

    /// <summary>
    /// Changes what signing in as this person does. Null puts them back to approving.
    /// </summary>
    /// <remarks>
    /// The rule is the only field that changes after creation, and deliberately: the personal
    /// number is derived from the date of birth, so moving one without the other would produce
    /// somebody whose number disagrees with their age. Returns null if there is nobody by that
    /// name.
    /// </remarks>
    public async Task<StubIdCitizen?> SetRuleAsync(
        string id, string? rule, CancellationToken ct = default)
    {
        using var response = await http.PatchAsJsonAsync(
            $"/_stubid/v1/citizens/{Uri.EscapeDataString(id)}",
            new SetRuleBody(rule),
            ControlJson.Default.SetRuleBody,
            ct);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await Control.ReadAsync(response, ControlJson.Default.StubIdCitizen, ct);
    }

    /// <summary>Removes one. False when there was nobody by that name.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/_stubid/v1/citizens/{Uri.EscapeDataString(id)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await Control.EnsureAsync(response, ct);

        return true;
    }
}

/// <summary>The logins themselves.</summary>
public sealed class SessionApi(HttpClient http)
{
    /// <summary>
    /// Every login, most recent first.
    /// </summary>
    /// <remarks>
    /// Reading expires anything past its deadline, so this is a clock tick as well as a query.
    /// </remarks>
    public async Task<IReadOnlyList<StubIdSession>> ListAsync(
        SessionState? state = null, string? clientId = null, CancellationToken ct = default)
    {
        var query = new List<string>();

        if (state is not null)
        {
            query.Add($"state={state}");
        }

        if (clientId is not null)
        {
            query.Add($"clientId={Uri.EscapeDataString(clientId)}");
        }

        var path = "/_stubid/v1/sessions" + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        using var response = await http.GetAsync(path, ct);

        return await Control.ReadAsync(response, ControlJson.Default.IReadOnlyListStubIdSession, ct);
    }

    /// <summary>One login, or null if there is no such login.</summary>
    public async Task<StubIdSession?> FindAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/_stubid/v1/sessions/{Uri.EscapeDataString(id)}", ct);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await Control.ReadAsync(response, ControlJson.Default.StubIdSession, ct);
    }

    /// <summary>
    /// Why the login went the way it did, tier by tier, or null if there is no such login.
    /// </summary>
    public async Task<SessionExplanation?> ExplainAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"/_stubid/v1/sessions/{Uri.EscapeDataString(id)}/explain", ct);

        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await Control.ReadAsync(response, ControlJson.Default.SessionExplanation, ct);
    }

    /// <summary>
    /// Approves one login, as the named citizen or as the default one.
    /// </summary>
    /// <remarks>
    /// A successful call is not the same as an approval. The chosen citizen's own rule decides what
    /// approving them means, so a person created to abort produces
    /// <see cref="DecisionOutcome.State" /> of <see cref="SessionState.Failed" /> from a call that
    /// did exactly what it was asked. Read the state, not the absence of an exception.
    /// </remarks>
    public async Task<DecisionOutcome> ApproveAsync(
        string id, string? citizenId = null, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{Uri.EscapeDataString(id)}/approve",
            new ApproveBody(citizenId),
            ControlJson.Default.ApproveBody,
            ct);

        return await OutcomeAsync(response, ct);
    }

    /// <summary>Refuses one login with a broker error code.</summary>
    public async Task<DecisionOutcome> RejectAsync(
        string id,
        string errorCode = "mitid_user_aborted",
        string error = "access_denied",
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"/_stubid/v1/sessions/{Uri.EscapeDataString(id)}/reject",
            new RejectBody(errorCode, error),
            ControlJson.Default.RejectBody,
            ct);

        return await OutcomeAsync(response, ct);
    }

    private static async Task<DecisionOutcome> OutcomeAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        // Losing the race is an answer, not a failure: the winner's outcome is what both writers
        // needed to know, and it is on this response. Read straight off it rather than through the
        // usual helper, which would see a 409 and raise the exception this branch exists to avoid.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync(
                ControlJson.Default.ConflictBody, ct);

            return new DecisionOutcome
            {
                Decided = false,
                Detail = conflict?.Detail,
                Outcome = conflict?.Outcome,
                State = conflict?.Outcome?.State,
            };
        }

        var decided = await Control.ReadAsync(response, ControlJson.Default.DecidedBody, ct);

        return new DecisionOutcome
        {
            Decided = decided.Decided,
            State = decided.State,
            CitizenId = decided.Citizen,
        };
    }
}

/// <summary>Outcomes queued ahead of the logins they resolve.</summary>
public sealed class BehaviourApi(HttpClient http)
{
    /// <summary>
    /// Queues one outcome for the next matching login, consumed once.
    /// </summary>
    /// <remarks>
    /// This is the primitive a suite in CI wants: queue the outcome, drive the application, and the
    /// login is decided before anything could have waited on it. Nothing is returned because
    /// nothing has happened yet - there is no login to report on.
    /// </remarks>
    public async Task EnqueueAsync(Decision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        using var response = await http.PostAsJsonAsync(
            "/_stubid/v1/behaviours/enqueue",
            new EnqueueBody(
                decision.Approve, decision.ClientId, decision.CitizenId, decision.ErrorCode, decision.Error),
            ControlJson.Default.EnqueueBody,
            ct);

        await Control.EnsureAsync(response, ct);
    }

    /// <summary>
    /// What is still queued, in the order it will be taken. Reading does not consume it.
    /// </summary>
    /// <remarks>
    /// A decision queued by one test and spent by the next is the failure this tier otherwise
    /// prevents, and it used to be invisible: the queue could be written and cleared and never
    /// read. This is what a suite asserts on when an outcome arrives that nobody asked for.
    /// </remarks>
    public async Task<IReadOnlyList<QueuedDecision>> ListAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/_stubid/v1/behaviours", ct);
        var body = await Control.ReadAsync(response, ControlJson.Default.QueuedBody, ct);

        return body.Queued;
    }

    /// <summary>Drops everything still queued.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync("/_stubid/v1/behaviours", ct);

        await Control.EnsureAsync(response, ct);
    }
}

/// <summary>The clock, when the instance was started with one a test can move.</summary>
public sealed class ClockApi(HttpClient http)
{
    /// <summary>
    /// What the instance thinks the time is, and whether it can be moved.
    /// </summary>
    /// <remarks>
    /// Reads rather than moves, which <see cref="AdvanceAsync" /> could not do: advancing by
    /// nothing still needs a controllable clock, so there was no way to ask the question of an
    /// ordinary instance at all.
    /// </remarks>
    public async Task<StubIdClock> ReadAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/_stubid/v1/time", ct);

        return await Control.ReadAsync(response, ControlJson.Default.StubIdClock, ct);
    }

    /// <summary>
    /// Moves the clock forward and answers with the time it now reads.
    /// </summary>
    /// <remarks>
    /// Throws unless the instance was started with a controllable clock; the exception carries
    /// StubID's own instruction for which setting to change.
    /// </remarks>
    public async Task<DateTimeOffset> AdvanceAsync(TimeSpan by, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "/_stubid/v1/time/advance",
            new AdvanceBody(by.TotalSeconds),
            ControlJson.Default.AdvanceBody,
            ct);

        var body = await Control.ReadAsync(response, ControlJson.Default.NowBody, ct);

        return body.Now;
    }
}

/// <summary>What the instance knows about itself.</summary>
public sealed class RuntimeApi(HttpClient http)
{
    /// <summary>
    /// The public half of the certificate this instance serves TLS with, or null when it serves
    /// plain HTTP.
    /// </summary>
    /// <remarks>
    /// Fetched over whichever transport this client already reaches, which is how a caller learns
    /// what to expect on the secured one before it has any basis for trusting it. The private key is
    /// not exposed and there is no route that would reach it.
    /// </remarks>
    public async Task<X509Certificate2?> GetTlsCertificateAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/_stubid/v1/runtime/tls-certificate", ct);

        var body = await Control.ReadAsync(response, ControlJson.Default.TlsCertificateBody, ct);

        return body.Certificate is null
            ? null
            : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(body.Certificate));
    }

    /// <summary>The address this instance answers at, or null if nothing has told it.</summary>
    public async Task<Uri?> GetPublicBaseUrlAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/_stubid/v1/runtime/public-base-url", ct);

        var body = await Control.ReadAsync(response, ControlJson.Default.PublicBaseUrlBody, ct);

        return body.PublicBaseUrl is null ? null : new Uri(body.PublicBaseUrl);
    }

    /// <summary>
    /// Tells the instance the address its callers reach it at, which every issuer it emits is built
    /// from.
    /// </summary>
    /// <remarks>
    /// The last call wins. Configuration seeds the value rather than locking it, because the case
    /// this exists for is the one where the correct address could not be known when the process
    /// started - a container does not learn its own mapped host port until Docker has started it.
    /// </remarks>
    public async Task<Uri> SetPublicBaseUrlAsync(Uri value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var response = await http.PutAsJsonAsync(
            "/_stubid/v1/runtime/public-base-url",
            new PublicBaseUrlBody(value.ToString()),
            ControlJson.Default.PublicBaseUrlBody,
            ct);

        var body = await Control.ReadAsync(response, ControlJson.Default.PublicBaseUrlBody, ct);

        return new Uri(body.PublicBaseUrl!);
    }
}
