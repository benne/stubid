using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace StubId.CaptureHarness;

/// <summary>What was sent, so the callback can be matched to it and the code exchanged.</summary>
internal sealed record Pending(ManualCase Case, string Verifier, string Nonce, string AuthorizeUrl);

/// <summary>
/// A relying party that records a real login.
/// </summary>
/// <remarks>
/// The MitID widget blocks browser automation on purpose, so a person drives the browser and
/// this records what crosses the wire. It deliberately does not use a client library: a
/// library would present a tidied view of the exchange, and the bytes are the point.
/// </remarks>
public static class Session
{
    private const string Authority = "https://pp.netseidbroker.dk/op";
    private const string RedirectUri = "http://localhost:5099/callback";

    private static readonly ConcurrentDictionary<string, Pending> Pendings = new(StringComparer.Ordinal);

    public static async Task<int> RunAsync(FixtureStore store)
    {
        var staging = new Staging();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://localhost:5099");
        var app = builder.Build();

        app.MapGet("/", () => Results.Text(Launchpad(staging), "text/html; charset=utf-8"));

        app.MapGet("/start/{id}", (string id) =>
        {
            var @case = ManualCatalogue.All.FirstOrDefault(c => c.Id == id);
            if (@case is null)
            {
                return Results.NotFound($"No case {id}.");
            }

            var (url, verifier, nonce) = BuildAuthorize(@case);

            Pendings[@case.Id] = new Pending(@case, verifier, nonce, url);
            return Results.Redirect(url);
        });

        // Both verbs: form_post arrives as a POST, and they are different code paths. A
        // refusal arrives here too, carrying error and no code, and losing that would lose
        // the recording the step existed to make.
        app.MapMethods("/callback", ["GET", "POST"], async (HttpContext http) =>
        {
            var parameters = await ReadParameters(http);
            parameters.TryGetValue("state", out var state);

            if (state is null || !Pendings.TryGetValue(state, out var pending))
            {
                return Results.Text(
                    Page("Unexpected callback", "No pending request matches this state. "
                        + "Start the step from the launchpad rather than replaying a URL."),
                    "text/html; charset=utf-8");
            }

            staging.Add(pending.Case, "callback", FrontChannel(http, parameters, pending));

            if (parameters.TryGetValue("error", out var error))
            {
                var description = parameters.GetValueOrDefault("error_description", "");
                return Results.Text(
                    Page($"{pending.Case.Id} refused, and that is recorded",
                        $"error={WebUtility.HtmlEncode(error)}<br>"
                        + $"error_description={WebUtility.HtmlEncode(description)}"),
                    "text/html; charset=utf-8");
            }

            if (!parameters.TryGetValue("code", out var code))
            {
                return Results.Text(
                    Page($"{pending.Case.Id}: no code and no error",
                        "The response carried neither. Worth looking at before continuing."),
                    "text/html; charset=utf-8");
            }

            // Immediately: codes are single use and short lived.
            var report = await ExchangeAsync(pending, code, staging, http.RequestAborted);
            return Results.Text(Page($"{pending.Case.Id} recorded", report), "text/html; charset=utf-8");
        });

        app.MapGet("/finish", async (HttpContext http) =>
        {
            var suspicious = staging.Suspicious();
            if (suspicious.Count > 0 && !http.Request.Query.ContainsKey("anyway"))
            {
                return Results.Text(
                    Page("Something is unaccounted for",
                        string.Join("<br>", suspicious.Select(WebUtility.HtmlEncode))
                        + "<br><br>Add it to the redact block in capture.local.json and restart, "
                        + "or continue with <a href=\"/finish?anyway=1\">/finish?anyway=1</a> "
                        + "if you are sure."),
                    "text/html; charset=utf-8");
            }

            var written = await staging.WriteAsync(store, http.RequestAborted);
            await store.WriteManifestAsync(
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), http.RequestAborted);

            return Results.Text(
                Page("Written", $"{written} exchanges written to {store.Root}."),
                "text/html; charset=utf-8");
        });

        Console.WriteLine("Recording session on http://localhost:5099 - open it in a browser.");
        Console.WriteLine("Finish with http://localhost:5099/finish, which writes the fixtures.");
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Builds the authorize request for a step. Shared with the rehearsal, so what is checked
    /// beforehand is exactly what the sitting sends.
    /// </summary>
    public static (string Url, string Verifier, string Nonce) BuildAuthorize(ManualCase @case)
    {
        var verifier = Base64UrlText(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64UrlText(RandomNumberGenerator.GetBytes(16));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = ClientId(@case.Client),
            ["response_type"] = @case.ResponseType,
            ["redirect_uri"] = @case.RedirectUriOverride ?? RedirectUri,
            ["scope"] = @case.Scope,
            ["state"] = @case.Id,
            ["nonce"] = nonce,
            ["idp_values"] = "mitid",
            ["code_challenge"] = Base64UrlText(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };

        if (@case.ResponseMode is not null)
        {
            parameters["response_mode"] = @case.ResponseMode;
        }

        foreach (var (key, value) in @case.Extra)
        {
            parameters[key] = value;
        }

        var url = $"{Authority}/connect/authorize?" + string.Join('&',
            parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        return (url, verifier, nonce);
    }

    private static async Task<string> ExchangeAsync(
        Pending pending, string code, Staging staging, CancellationToken ct)
    {
        using var recorder = new RecordingHandler(new HttpClientHandler { AllowAutoRedirect = false });
        using var client = new HttpClient(recorder);

        using var response = await client.PostAsync($"{Authority}/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = pending.Verifier,
                ["client_id"] = ClientId(pending.Case.Client),
                ["client_secret"] = Secret(pending.Case.Client),
            }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        staging.Discover("CODE", code);
        staging.DiscoverIn(body);

        var accessToken = Member(body, "access_token");

        foreach (var followUp in pending.Case.FollowUps)
        {
            await RunFollowUpAsync(client, followUp, accessToken, code, pending, staging, ct);
        }

        foreach (var exchange in recorder.Exchanges)
        {
            staging.Add(pending.Case, Name(exchange), exchange);
        }

        return $"{recorder.Exchanges.Count} back-channel exchanges recorded. "
            + $"<a href=\"/\">Back to the list</a>.";
    }

    private static async Task RunFollowUpAsync(
        HttpClient client, FollowUp followUp, string? accessToken, string code,
        Pending pending, Staging staging, CancellationToken ct)
    {
        switch (followUp)
        {
            case FollowUp.UserInfo when accessToken is not null:
                using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Authority}/connect/userinfo"))
                {
                    request.Headers.Authorization = new("Bearer", accessToken);
                    using var response = await client.SendAsync(request, ct);
                    staging.DiscoverIn(await response.Content.ReadAsStringAsync(ct));
                }

                break;

            case FollowUp.ReplayCode:
                // What the broker does when a code is presented twice. IdentityServer is
                // documented to revoke the whole grant, which is worth having recorded.
                using (await client.PostAsync($"{Authority}/connect/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = RedirectUri,
                        ["code_verifier"] = pending.Verifier,
                        ["client_id"] = ClientId(pending.Case.Client),
                        ["client_secret"] = Secret(pending.Case.Client),
                    }), ct))
                {
                }

                break;

            case FollowUp.CprMatch when accessToken is not null:
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{Authority}/api/v1/mitid/matchCpr"))
                {
                    request.Headers.Authorization = new("Bearer", accessToken);
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request, ct);
                }

                break;

            case FollowUp.EndSession:
                using (await client.GetAsync($"{Authority}/connect/endsession", ct))
                {
                }

                break;
        }
    }

    private static RecordedExchange FrontChannel(
        HttpContext http, IDictionary<string, string> parameters, Pending pending)
    {
        var body = Encoding.UTF8.GetBytes(string.Join('\n',
            parameters.Select(p => $"{p.Key}={p.Value}")));

        return new RecordedExchange(
            http.Request.Method,
            pending.AuthorizeUrl,
            [],
            null,
            (int)HttpStatusCode.OK,
            "callback",
            [.. http.Request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString()))],
            body);
    }

    private static async Task<Dictionary<string, string>> ReadParameters(HttpContext http)
    {
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            return form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);
        }

        return http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
    }

    private static string Name(RecordedExchange exchange) => exchange.Url switch
    {
        var u when u.Contains("/connect/token", StringComparison.Ordinal) => "token",
        var u when u.Contains("/connect/userinfo", StringComparison.Ordinal) => "userinfo",
        var u when u.Contains("/matchCpr", StringComparison.Ordinal) => "cpr-match",
        var u when u.Contains("/connect/endsession", StringComparison.Ordinal) => "endsession",
        _ => "other",
    };

    private static string? Member(string json, string name)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string ClientId(ClientProfile profile) => profile switch
    {
        ClientProfile.OpenCode => CaptureCatalogue.OpenCodeClient,
        ClientProfile.OpenImplicit => "93ed8e0d-93ad-405c-b1ac-8bf13d484941",
        ClientProfile.Restricted => LocalSettings.Get("STUBID_NEB_PP_RESTRICTED_CLIENT_ID")
            ?? throw new InvalidOperationException(
                "Set STUBID_NEB_PP_RESTRICTED_CLIENT_ID to record the unregistered-redirect case."),
        _ => LocalSettings.Get("STUBID_NEB_PP_CLIENT_ID")
             ?? throw new InvalidOperationException(
                 "Set STUBID_NEB_PP_CLIENT_ID to record with the private client."),
    };

    private static string Secret(ClientProfile profile) => profile switch
    {
        ClientProfile.OpenCode or ClientProfile.OpenImplicit =>
            LocalSettings.Get("STUBID_NEB_PP_CODE_CLIENT_SECRET")
            ?? throw new InvalidOperationException("Set STUBID_NEB_PP_CODE_CLIENT_SECRET."),
        ClientProfile.Restricted => LocalSettings.Get("STUBID_NEB_PP_RESTRICTED_CLIENT_SECRET")
            ?? throw new InvalidOperationException("Set STUBID_NEB_PP_RESTRICTED_CLIENT_SECRET."),
        _ => LocalSettings.Get("STUBID_NEB_PP_CLIENT_SECRET")
             ?? throw new InvalidOperationException("Set STUBID_NEB_PP_CLIENT_SECRET."),
    };

    private static string Base64UrlText(byte[] bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    private static string Launchpad(Staging staging)
    {
        var rows = string.Join("\n", ManualCatalogue.All.Select(c =>
        {
            var done = staging.Recorded.Contains(c.Id) ? "recorded" : "";
            return $"""
                <tr>
                  <td><a href="/start/{c.Id}">{c.Id}</a></td>
                  <td>{WebUtility.HtmlEncode(c.Step)}</td>
                  <td>{WebUtility.HtmlEncode(c.Title)}</td>
                  <td>{WebUtility.HtmlEncode(c.Operator)}</td>
                  <td>{done}</td>
                </tr>
                """;
        }));

        return Page("Recording session", $"""
            <p>Work down the list <strong>in the order shown</strong>, which is the step order,
            not the case number. A login establishes a broker session, so the steps that record
            a refusal or an abort come first: run them after a successful login and they record
            something else.</p>
            <p>Each link starts one step; the browser goes to the broker, you complete it, and
            the exchange is recorded here.</p>
            <p>Nothing is written to disk until you finish: values born during the sitting
            appear in exchanges recorded before the response that names them, so scrubbing can
            only be done once over the whole set.</p>
            <table border="1" cellpadding="6" cellspacing="0">
            <tr><th>Case</th><th>Step</th><th>What it records</th><th>What you do</th><th></th></tr>
            {rows}
            </table>
            <p><a href="/finish">Finish and write the fixtures</a> ({staging.Count} exchanges staged)</p>
            """);
    }

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{font:14px system-ui;margin:2rem;max-width:60rem}td,th{text-align:left}</style>
        </head><body><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</body></html>
        """;
}
