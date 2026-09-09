using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using StubId.InProcess;

namespace StubId.Release.Tests;

/// <summary>
/// What StubID's own API puts on the wire, read from the bytes rather than from the types.
/// </summary>
/// <remarks>
/// The gap this closes was named in <c>docs/compatibility.md</c> before it was closed: the control
/// API's field names are derived from property names by a naming policy and exercised through the
/// typed client's own records, so renaming a property renamed both halves at once and every test
/// went on passing. A suite written in Node or Spring, or one reading the JSON by hand, would have
/// been broken by a change nothing here could see.
/// <para>
/// So an instance is started and driven through a script that reaches every route, and what is
/// recorded is the key path of every field in the response it actually sent. Paths and not types:
/// a nullable field's JSON kind depends on which state the script had the instance in when it was
/// read, and a baseline that changes with the fixture is one nobody can trust. A rename or a
/// removal is what this catches, which is the failure the promise is about.
/// </para>
/// </remarks>
internal static class ControlSurface
{
    /// <summary>The routes StubID answers for itself, minus the pages a person looks at.</summary>
    /// <remarks>
    /// The admin UI is under the same prefix and is deliberately outside this: it is HTML for a
    /// person, has no version, and the compatibility statement promises nothing about it.
    /// </remarks>
    private const string Admin = "/_stubid/admin";

    /// <summary>
    /// Where every host this assembly starts keeps its signing keys.
    /// </summary>
    /// <remarks>
    /// Its own directory rather than the shared default, which every other in-process host in this
    /// repository writes to and which two test assemblies running at once would both write to. The
    /// keys are deliberately kept between runs rather than made fresh: what is recorded here is
    /// field names and not key material, so reuse changes nothing in the file, and generating four
    /// key pairs per composition would make a fast test slow for no answer it would change.
    /// </remarks>
    internal static string KeyPath => Path.Combine(Path.GetTempPath(), "stubid-control-surface");

    /// <summary>What a route on its way out is marked with, here and in the shipped file.</summary>
    internal const string DeprecatedMark = "(deprecated)";

    private const string Client = "0a775a87-878c-4b83-abe3-ee29c720c3e7";
    private const string Redirect = "http://localhost:5099/callback";

    /// <summary>Every response the script saw, keyed by the route and status that produced it.</summary>
    private sealed record Observed(string Method, string Pattern, int Status)
    {
        public SortedSet<string> Paths { get; } = new(StringComparer.Ordinal);

        /// <summary>How the response is described when it has no field names to list.</summary>
        public string? Note { get; set; }

        /// <summary>Whether the route said, on the wire, that it is going away.</summary>
        /// <remarks>
        /// Recorded in the file rather than read from the running build, because the question the
        /// removal rule asks is whether the route carried notice <em>when it shipped</em>. A route
        /// that has since been removed is not registered at all, so asking a live instance could
        /// only ever answer no.
        /// </remarks>
        public bool Deprecated { get; set; }
    }

    internal static async Task<string> ComposeAsync(CancellationToken ct)
    {
        await using var stub = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("http://localhost"))
            .WithControllableClock()
            .WithAutomaticApproval(false)
            .WithKeyPath(KeyPath)
            .Build();

        await stub.StartAsync(ct);

        using var http = stub.CreateClient();

        var seen = new Dictionary<(string, string, int), Observed>();

        await Drive(http, seen, ct);

        var registered = Registered(stub);
        var lines = new List<string>();

        foreach (var observed in seen.Values
            .OrderBy(entry => entry.Pattern, StringComparer.Ordinal)
            .ThenBy(entry => entry.Method, StringComparer.Ordinal)
            .ThenBy(entry => entry.Status))
        {
            lines.Add($"{observed.Method} {observed.Pattern} -> {observed.Status}"
                      + (observed.Note is null ? "" : " " + observed.Note)
                      + (observed.Deprecated ? " " + DeprecatedMark : ""));

            // An array seen empty once and populated later leaves both marks; the empty one is
            // dropped, because "this collection exists" is already said by the fields under it.
            lines.AddRange(observed.Paths
                .Where(path => !observed.Paths.Any(other =>
                    other.StartsWith(path + ".", StringComparison.Ordinal)))
                .Select(path => "  " + path));
            lines.Add("");
        }

        // Never AppendLine: it writes Environment.NewLine, which made a generated file in this
        // same project pass on Linux and fail on Windows with a diff whose halves read identically.
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    /// <summary>Every control route this build registers, as the router knows it.</summary>
    /// <remarks>
    /// Read from the endpoint data source rather than from a list beside it, so a route added and
    /// not driven is a failure rather than an omission. The composite carries the emulated broker's
    /// routes too, which is why the prefix is filtered rather than trusted.
    /// </remarks>
    internal static IReadOnlyList<(string Method, string Pattern)> Registered(StubIdHost stub) =>
        stub.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } text
                               && text.StartsWith("/_stubid", StringComparison.Ordinal)
                               && !text.StartsWith(Admin, StringComparison.Ordinal))
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (Method: method, Pattern: endpoint.RoutePattern.RawText!)))
            .Distinct()
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The script: one instance taken through enough states that every route answers.
    /// </summary>
    /// <remarks>
    /// Order matters and the comments say why where it is not obvious. An empty collection teaches
    /// this nothing - a route read before anything populates it records no element fields at all -
    /// so the reads come after the writes that fill them.
    /// <para>
    /// Some branches are deliberately not reached and are named here rather than left to be
    /// noticed: the 503 body of the readiness probe needs an instance with no address, which the
    /// in-process builder cannot make; the certificate routes' populated shape needs TLS, which it
    /// refuses to configure; and the clock's refusal needs an uncontrollable clock, which is the
    /// opposite of what the sessions here need. Each would take a second instance for one shape.
    /// </para>
    /// </remarks>
    private static async Task Drive(
        HttpClient http, Dictionary<(string, string, int), Observed> seen, CancellationToken ct)
    {
        // Read-only routes that answer on a bare instance.
        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/fidelity", "/_stubid/v1/fidelity", ct);
        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/clients", "/_stubid/v1/clients", ct);
        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/routes", "/_stubid/v1/routes", ct);
        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/time", "/_stubid/v1/time", ct);
        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/issued", "/_stubid/v1/issued", ct);

        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/runtime/automatic-approval", "/_stubid/v1/runtime/automatic-approval", ct);
        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/runtime/public-base-url", "/_stubid/v1/runtime/public-base-url", ct);
        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/runtime/tls-certificate", "/_stubid/v1/runtime/tls-certificate", ct);
        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/runtime/tls-certificate.pem", "/_stubid/v1/runtime/tls-certificate.pem", ct);

        // The probes, which sit outside the versioned group on purpose and answer with no body.
        await Record(http, seen, HttpMethod.Get, "/_stubid/health/live", "/_stubid/health/live", ct);
        await Record(http, seen, HttpMethod.Get, "/_stubid/health/ready", "/_stubid/health/ready", ct);

        // A citizen of our own, so the created and amended shapes are both seen.
        await Record(http, seen, HttpMethod.Post, "/_stubid/v1/citizens", "/_stubid/v1/citizens", ct,
            Body(new
            {
                name = "Karen Refsgaard",
                dateOfBirth = "1979-11-02",
                gender = "female",
                id = "baseline",
                userName = "baseline",
                rule = "mitid_user_aborted",
            }));

        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/citizens", "/_stubid/v1/citizens", ct);
        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/citizens/baseline", "/_stubid/v1/citizens/{id}", ct);
        await Record(http, seen, HttpMethod.Patch,
            "/_stubid/v1/citizens/baseline", "/_stubid/v1/citizens/{id}", ct,
            Body(new { rule = "mitid_timeout" }));

        // A queued outcome, then the queue, which is empty until there is one.
        await Record(http, seen, HttpMethod.Post,
            "/_stubid/v1/behaviors/enqueue", "/_stubid/v1/behaviors/enqueue", ct,
            Body(new { approve = true, clientId = Client, citizenId = "default" }));

        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/behaviors", "/_stubid/v1/behaviors", ct);
        await Record(http, seen, HttpMethod.Delete, "/_stubid/v1/behaviors", "/_stubid/v1/behaviors", ct);

        // A login that stops and waits, which is what makes the session routes answer.
        var session = await Park(http, ct);

        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/sessions", "/_stubid/v1/sessions", ct);
        await Record(http, seen, HttpMethod.Get,
            $"/_stubid/v1/sessions/{session}", "/_stubid/v1/sessions/{id}", ct);

        // Read before deciding: the ladder carries a step whose tier is written as null, and it is
        // the undecided reading that has it.
        await Record(http, seen, HttpMethod.Get,
            $"/_stubid/v1/sessions/{session}/explain", "/_stubid/v1/sessions/{id}/explain", ct);

        await Record(http, seen, HttpMethod.Post,
            $"/_stubid/v1/sessions/{session}/approve", "/_stubid/v1/sessions/{id}/approve", ct,
            Body(new { citizenId = "default" }));

        // The same session again, now that it is decided: the conflict body, which nests a whole
        // session under a key of its own and is the largest shape this API has.
        await Record(http, seen, HttpMethod.Post,
            $"/_stubid/v1/sessions/{session}/reject", "/_stubid/v1/sessions/{id}/reject", ct,
            Body(new { errorCode = "mitid_user_aborted", error = "access_denied" }));

        // A second login, so the plain rejection body is seen as well as the conflict - and, while
        // it is still undecided, the refusal a caller gets for naming somebody who is not there.
        var second = await Park(http, ct);

        await Record(http, seen, HttpMethod.Post,
            $"/_stubid/v1/sessions/{second}/approve", "/_stubid/v1/sessions/{id}/approve", ct,
            Body(new { citizenId = "nobody-by-that-name" }));

        await Record(http, seen, HttpMethod.Post,
            $"/_stubid/v1/sessions/{second}/reject", "/_stubid/v1/sessions/{id}/reject", ct,
            Body(new { errorCode = "mitid_user_aborted", error = "access_denied" }));

        // A pushed request, purely so that something has been handed out: a parked login issues
        // nothing, and an empty collection records no element fields at all - which would leave
        // every field name of an artifact unguarded while the file looked complete.
        using (var pushed = await http.PostAsync("/op/connect/par", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", Client),
            new KeyValuePair<string, string>("client_secret", "any"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", Redirect),
            new KeyValuePair<string, string>("scope", "openid mitid"),
            new KeyValuePair<string, string>("state", "s"),
        ]), ct))
        {
            pushed.EnsureSuccessStatusCode();
        }

        await Record(http, seen, HttpMethod.Get, "/_stubid/v1/issued", "/_stubid/v1/issued", ct);

        // The settings that move while an instance runs.
        await Record(http, seen, HttpMethod.Put,
            "/_stubid/v1/runtime/automatic-approval", "/_stubid/v1/runtime/automatic-approval", ct,
            Body(new { enabled = true }));

        // Refused first, because the refusal carries field names of its own and the address has to
        // survive the attempt for everything after it.
        await Record(http, seen, HttpMethod.Put,
            "/_stubid/v1/runtime/public-base-url", "/_stubid/v1/runtime/public-base-url", ct,
            Body(new { publicBaseUrl = "http://localhost:18080/op" }));

        await Record(http, seen, HttpMethod.Put,
            "/_stubid/v1/runtime/public-base-url", "/_stubid/v1/runtime/public-base-url", ct,
            Body(new { publicBaseUrl = "http://localhost" }));

        await Record(http, seen, HttpMethod.Post, "/_stubid/v1/time/advance", "/_stubid/v1/time/advance", ct,
            Body(new { seconds = 1 }));

        // Asking for what is not there. All of these answer with no body, which is itself worth
        // recording: it says a caller gets a status and nothing to parse.
        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/sessions/no-such-login", "/_stubid/v1/sessions/{id}", ct);

        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/sessions/no-such-login/explain", "/_stubid/v1/sessions/{id}/explain", ct);

        await Record(http, seen, HttpMethod.Get,
            "/_stubid/v1/citizens/nobody", "/_stubid/v1/citizens/{id}", ct);

        // Last, because they take the instance apart.
        await Record(http, seen, HttpMethod.Delete,
            "/_stubid/v1/citizens/baseline", "/_stubid/v1/citizens/{id}", ct);

        await Record(http, seen, HttpMethod.Post, "/_stubid/v1/reset", "/_stubid/v1/reset", ct);
    }

    /// <summary>A login parked where somebody has to decide it, and its identifier.</summary>
    private static async Task<string> Park(HttpClient http, CancellationToken ct)
    {
        using var authorize = await http.GetAsync(
            $"/op/connect/authorize?client_id={Client}&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&scope=openid%20mitid&state=s&nonce=n", ct);

        using var listed = await http.GetAsync("/_stubid/v1/sessions?state=AwaitingApproval", ct);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(ct));

        return body.RootElement.EnumerateArray().Last().GetProperty("id").GetString()!;
    }

    private static HttpContent Body(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    /// <summary>Calls one route and records the shape of what came back.</summary>
    private static async Task Record(
        HttpClient http,
        Dictionary<(string, string, int), Observed> seen,
        HttpMethod method,
        string uri,
        string pattern,
        CancellationToken ct,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        using var response = await http.SendAsync(request, ct);

        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(ct);
        var deprecated = response.Headers.Contains("Deprecation");

        if (!seen.TryGetValue((method.Method, pattern, status), out var observed))
        {
            observed = new Observed(method.Method, pattern, status);
            seen[(method.Method, pattern, status)] = observed;
        }

        observed.Deprecated |= deprecated;

        if (string.IsNullOrEmpty(text))
        {
            observed.Note = "(no body)";

            return;
        }

        var type = response.Content.Headers.ContentType?.MediaType;

        if (type != "application/json")
        {
            // Recorded by its type alone: a certificate or a page has no field names to promise.
            observed.Note = $"({type})";

            return;
        }

        using var document = JsonDocument.Parse(text);

        Paths(document.RootElement, "", observed.Paths);
    }

    /// <summary>Every field in a body, as the path a caller would read it by.</summary>
    /// <remarks>
    /// An array contributes <c>[]</c> to the path and its elements are merged, so a collection
    /// describes one element shape rather than as many as the fixture happened to produce. An
    /// array observed empty records the path itself, so that a route reached with nothing in it is
    /// visibly not the same as a route never reached at all.
    /// </remarks>
    private static void Paths(JsonElement element, string prefix, SortedSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Paths(property.Value, prefix.Length == 0
                        ? property.Name
                        : prefix + "." + property.Name, into);
                }

                break;

            case JsonValueKind.Array:
                if (element.GetArrayLength() == 0)
                {
                    into.Add(prefix + "[]");
                }

                foreach (var item in element.EnumerateArray())
                {
                    Paths(item, prefix + "[]", into);
                }

                break;

            default:
                into.Add(prefix.Length == 0 ? "(a bare value)" : prefix);

                break;
        }
    }
}
