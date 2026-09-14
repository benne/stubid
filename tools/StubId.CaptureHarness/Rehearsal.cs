using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Sends every step's authorize request to the live broker, without completing any of them.
/// </summary>
/// <remarks>
/// A malformed parameter shows up as the broker's error page rather than its login page, and
/// finding that out with an operator in the chair costs the sitting's momentum and possibly a
/// second sitting. Nothing here authenticates anybody: each request stops at the point where
/// a person would take over.
/// </remarks>
public static partial class Rehearsal
{
    public static async Task<int> RunAsync(
        BrokerTarget broker, IReadOnlyList<ManualCase> cases, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler);

        var problems = 0;

        Console.WriteLine("Sending each step's authorize request. Nothing is completed.");
        Console.WriteLine();

        foreach (var @case in cases)
        {
            string url;
            try
            {
                (url, _, _) = Session.BuildAuthorize(broker, @case);
            }
            catch (InvalidOperationException error)
            {
                // A problem, not a pass: the summary used to say every step was ready after skipping
                // the seven a missing key made unbuildable.
                Console.WriteLine($"  {@case.Id}  PROBLEM   not sent: {error.Message.Split('.')[0]}");
                problems++;
                continue;
            }

            using var response = await client.GetAsync(url, ct);
            var location = response.Headers.Location?.ToString() ?? "";

            var (verdict, note) = Classify(broker, response.StatusCode, location, @case);
            Console.WriteLine($"  {@case.Id}  {verdict,-8}  {@case.Title}{note}");

            if (verdict == "PROBLEM")
            {
                problems++;
            }

            if (@case.SignRequest)
            {
                var (echo, why) = await RedirectsBackAsync(broker, client, @case, ct);
                Console.WriteLine($"  {@case.Id}  {echo,-8}  the redirect back carries its state{why}");

                if (echo == "PROBLEM")
                {
                    problems++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("Asking the token endpoint about each client that exchanges a code, with a code that is not real.");
        Console.WriteLine();
        problems += await CheckSecretsAsync(broker, client, cases, ct);

        Console.WriteLine();
        Console.WriteLine(problems == 0
            ? "Every step reaches the point where a person takes over."
            : $"{problems} problem(s) above. Fix them before sitting down.");

        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether a signed step comes back to us at all, and whether its state survives the trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A signed step sends only client_id, response_type and request in the query: its
    /// redirect_uri and its state are inside the object, and the sitting's callback matches a
    /// returning browser by state. Every measurement of a signed request so far stopped at the
    /// authorize response, so whether the broker resolves those two out of the object on the
    /// way back has never been seen. If it does not, a code that expires in seconds arrives
    /// unattributable and the authentication behind it is spent for nothing.
    /// </para>
    /// <para>
    /// Asking costs no authentication: prompt=none over a handler that keeps no cookies has no
    /// session to satisfy, so the broker refuses with login_required and refuses it by
    /// redirecting back to the client. That is the interaction-failure path standing in for the
    /// success path - the same object, validated the same way, ending differently - which is
    /// the most this can be had for free.
    /// </para>
    /// </remarks>
    private static async Task<(string Verdict, string Note)> RedirectsBackAsync(
        BrokerTarget broker, HttpClient client, ManualCase @case, CancellationToken ct)
    {
        var (url, _, _) = Session.BuildAuthorize(
            broker, @case, new Dictionary<string, string>(StringComparer.Ordinal) { ["prompt"] = "none" });

        using var response = await client.GetAsync(url, ct);
        var location = response.Headers.Location?.ToString() ?? "";

        // A form_post step is answered with a page that posts back, not with a redirect, so a 200 is
        // how the answer arrives. The second broker's hybrid step was reported as a problem for it.
        if (@case.ResponseMode == "form_post" && response.StatusCode == HttpStatusCode.OK)
        {
            var (action, state) = FormPostTarget(await response.Content.ReadAsStringAsync(ct));

            if (action is null || !action.StartsWith(Session.RedirectUri, StringComparison.Ordinal))
            {
                return ("PROBLEM", "  answered 200 with no form posting back to the client");
            }

            return state == @case.Id
                ? ("ready", "")
                : ("PROBLEM", "  posted back without it - the sitting's callback cannot match on state");
        }

        if (!location.StartsWith(Session.RedirectUri, StringComparison.Ordinal))
        {
            return ("PROBLEM", location.Length == 0
                ? $"  no redirect: {(int)response.StatusCode}"
                : $"  went to {location.Split('?')[0]} instead of the client");
        }

        return location.Contains($"state={@case.Id}", StringComparison.Ordinal)
            ? ("ready", "")
            : ("PROBLEM", "  came back without it - the sitting's callback cannot match on state");
    }

    /// <summary>
    /// Whether the broker accepts each client's secret, asked before a login is spent finding out.
    /// </summary>
    /// <remarks>
    /// A signed step never sends its secret until the code is exchanged, which is after the login. A
    /// code that is not real is refused for what it is only once the client has authenticated, so
    /// invalid_grant says the secret is good and invalid_client says it is not - which is the pair
    /// the unattended pack records for the partner client.
    /// </remarks>
    private static async Task<int> CheckSecretsAsync(
        BrokerTarget broker, HttpClient client, IReadOnlyList<ManualCase> cases, CancellationToken ct)
    {
        var problems = 0;

        var exchanging = cases
            .Where(c => c.ExpectCode && c.ResponseType.Split(' ').Contains("code"))
            .Select(c => c.Client)
            .Distinct();

        foreach (var registration in exchanging)
        {
            Dictionary<string, string> form;
            try
            {
                form = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = "not-a-real-code",
                    ["redirect_uri"] = Session.RedirectUri,
                    ["client_id"] = registration.ClientId(),
                    ["client_secret"] = registration.Secret(),
                };
            }
            catch (InvalidOperationException error)
            {
                Console.WriteLine($"  PROBLEM   the {registration.Name} client: {error.Message.Split('.')[0]}");
                problems++;
                continue;
            }

            using var response = await client.PostAsync(
                $"{broker.Authority}/connect/token", new FormUrlEncodedContent(form), ct);
            var (verdict, note) = SecretVerdict((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));

            Console.WriteLine($"  {verdict,-8}  the {registration.Name} client's secret{note}");
            if (verdict == "PROBLEM")
            {
                problems++;
            }
        }

        return problems;
    }

    /// <summary>What the token endpoint's answer to a code that is not real says about the secret.</summary>
    public static (string Verdict, string Note) SecretVerdict(int status, string body)
    {
        string? error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var member)
                && member.ValueKind == JsonValueKind.String)
            {
                error = member.GetString();
            }
        }
        catch (JsonException)
        {
            // Not an OAuth error at all, which is reported below as such.
        }

        return error switch
        {
            "invalid_grant" => ("ready", ""),
            "invalid_client" => ("PROBLEM", "  is refused, so the exchange after a login would fail"),
            null => ("PROBLEM", $"  could not be checked: {status} with no OAuth error"),
            _ => ("PROBLEM", $"  could not be checked: {error}"),
        };
    }

    /// <summary>Where a form_post page sends the browser, and the state it sends along.</summary>
    /// <remarks>Either quote style, and entity-encoded values decoded, since the page is HTML.</remarks>
    public static (string? Action, string? State) FormPostTarget(string html)
    {
        var action = FormAction().Match(html);
        var state = StateInput().Match(html);

        return (
            action.Success ? WebUtility.HtmlDecode(action.Groups["value"].Value) : null,
            state.Success ? WebUtility.HtmlDecode(state.Groups["value"].Value) : null);
    }

    [GeneratedRegex("""<form\b[^>]*\baction\s*=\s*(['"])(?<value>.*?)\1""", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [GeneratedRegex("""<input\b(?=[^>]*\bname\s*=\s*(['"])state\1)[^>]*\bvalue\s*=\s*(['"])(?<value>.*?)\2""", RegexOptions.IgnoreCase)]
    private static partial Regex StateInput();

    /// <summary>
    /// What a location means, read from the broker rather than from two literals kept here.
    /// </summary>
    /// <remarks>
    /// These were spelled out in this file and in the disposition classifier both, which is two
    /// places to edit and one of them to forget. They belong to the broker.
    /// </remarks>
    private static (string Verdict, string Note) Classify(
        BrokerTarget broker, HttpStatusCode status, string location, ManualCase @case)
    {
        if (broker.LoginMarkers.Any(marker => location.Contains(marker, StringComparison.Ordinal)))
        {
            return ("ready", "");
        }

        if (location.Contains(broker.ErrorMarker, StringComparison.Ordinal))
        {
            // The one step that is supposed to be refused: it exists to record the refusal.
            return @case.RedirectUriOverride is not null
                ? ("ready", "  (refused, which is the recording)")
                : ("PROBLEM", "  refused before the login page");
        }

        // A broker whose login path nobody has declared cannot reach the first branch, so this
        // is where an accepted request lands - and saying where it went is what fills the gap in.
        return broker.LoginMarkers.Count == 0 && location.Length > 0
            ? ("PROBLEM", $"  went to {location.Split('?')[0]}, and {broker.Display} declares no "
                + "login path. If that is where an accepted request lands, add it to LoginMarkers.")
            : ("PROBLEM", $"  unexpected: {(int)status} {location}");
    }
}
