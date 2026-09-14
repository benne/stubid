using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
    /// <summary>The broker this sitting is against, set once when the host starts.</summary>
    /// <remarks>
    /// A field rather than a parameter threaded through six helpers, because this process hosts
    /// one sitting against one broker and then exits. <see cref="BuildAuthorize" /> deliberately
    /// does not read it and takes the target instead: the rehearsal and the tests call that
    /// method without ever starting a host, and a method reading a static somebody else had to
    /// set would be right only when it happened to have been set.
    /// </remarks>
    private static BrokerTarget Target = BrokerTarget.NetsEidBroker;

    private static string Authority => Target.Authority;

    /// <summary>Where a step comes back to. The rehearsal checks the broker redirects here.</summary>
    public const string RedirectUri = "http://localhost:5099/callback";

    private static readonly ConcurrentDictionary<string, Pending> Pendings = new(StringComparer.Ordinal);

    public static async Task<int> RunAsync(
        BrokerTarget broker, FixtureStore store, IReadOnlyList<ManualCase> cases)
    {
        Target = broker;

        // Fetched now, not read from the committed CAP-002. Whether a token's signature checks
        // out against the broker's published key is the one fact about this sitting that cannot
        // be established afterwards - the transaction-signing key already rotated once, in May
        // 2026 - and a stale key set would answer "did not verify" for the wrong reason.
        var jwks = await FetchJwksAsync();
        Console.WriteLine(jwks is null
            ? "The key set could not be fetched. Signatures will be recorded as unchecked."
            : "Fetched today's key set. Signatures will be checked as they are recorded.");

        var staging = new Staging(jwks);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://localhost:5099");
        var app = builder.Build();

        app.MapGet("/", () => Results.Text(Launchpad(cases, staging), "text/html; charset=utf-8"));

        app.MapGet("/start/{id}", (string id) =>
        {
            var @case = cases.FirstOrDefault(c => c.Id == id);
            if (@case is null)
            {
                return Results.NotFound($"No case {id}.");
            }

            var (url, verifier, nonce) = BuildAuthorize(Target, @case);

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

            // Removed rather than read: reloading the callback would otherwise stage the
            // same exchange again, and the count is how the operator knows what was captured.
            Pending? pending = null;
            string? note = null;

            if (CallbackStep(state, [.. Pendings.Values.Select(p => p.Case)]) is { } id
                && Pendings.TryRemove(id, out var matched))
            {
                pending = matched;
                note = state is null
                    ? "The callback carried no state, and was matched to the only step outstanding "
                      + "that expects a code."
                    : null;
            }

            if (pending is null)
            {
                // What arrived, so a refusal nobody was waiting for can still be written down. Never
                // the code.
                var arrived = string.Join("<br>", new[] { "state", "error", "error_description" }
                    .Where(parameters.ContainsKey)
                    .Select(name => $"<code>{name}={WebUtility.HtmlEncode(parameters[name])}</code>"));

                return Results.Text(
                    Page("Unexpected callback",
                        "<p>No pending request matches this state. Start the step from the list "
                        + "rather than reloading this page: a step is consumed once it "
                        + "completes, so a reload cannot record it twice.</p>"
                        + (arrived.Length == 0 ? "" : $"<p>{arrived}</p>")
                        + "<p><a href=\"/\">Back to the list</a></p>"),
                    "text/html; charset=utf-8");
            }

            staging.Add(pending.Case, "callback", FrontChannel(http, parameters, pending), note);

            if (parameters.TryGetValue("error", out var error))
            {
                var description = parameters.GetValueOrDefault("error_description", "");
                return Results.Text(
                    Page($"{pending.Case.Id} refused, and that is recorded",
                        $"<p><code>error={WebUtility.HtmlEncode(error)}</code><br>"
                        + $"<code>error_description={WebUtility.HtmlEncode(description)}</code></p>"
                        + "<p>A refusal is a successful recording: this is the step's whole "
                        + "purpose.</p><p><a href=\"/\">Back to the list</a></p>"),
                    "text/html; charset=utf-8");
            }

            if (!parameters.TryGetValue("code", out var code))
            {
                return Results.Text(
                    Page($"{pending.Case.Id}: no code and no error",
                        "<p>The response carried neither. Worth looking at before continuing.</p>"
                        + "<p><a href=\"/\">Back to the list</a></p>"),
                    "text/html; charset=utf-8");
            }

            // Immediately: codes are single use and short lived.
            var report = await ExchangeAsync(pending, code, staging, http.RequestAborted);
            return Results.Text(Page($"{pending.Case.Id} recorded", report), "text/html; charset=utf-8");
        });

        app.MapGet("/staged", () =>
        {
            var rows = string.Join("\n", staging.Preview().Select(e => $"""
                <h3>{WebUtility.HtmlEncode(e.Case)} — {WebUtility.HtmlEncode(e.Exchange)} ({e.Status})</h3>
                <pre style="white-space:pre-wrap;word-break:break-all;background:#f4f4f4;padding:1rem">{WebUtility.HtmlEncode(
                    e.Body.Length > 4000 ? e.Body[..4000] + "\n… truncated" : e.Body)}</pre>
                """));

            return Results.Text(
                Page("Staged so far", rows.Length == 0
                    ? "<p>Nothing recorded yet.</p><p><a href=\"/\">Back to the list</a></p>"
                    : $"<p>Scrubbed, as it would be written.</p><p><a href=\"/\">Back to the list</a></p>{rows}"),
                "text/html; charset=utf-8");
        });

        // Ends the broker session without recording anything, so a step that needs a fresh
        // authentication can get one. The recorded logout is CAP-027; this is housekeeping.
        app.MapGet("/logout", async () =>
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var response = await client.GetAsync($"{Authority}/connect/endsession");

            return Results.Text(
                Page("Broker session ended", "<p>The next step will authenticate from scratch. "
                    + "This was not recorded, and is not the logout StubID reproduces.</p>"
                    + "<p><a href=\"/\">Back to the list</a></p>"),
                "text/html; charset=utf-8");
        });

        app.MapGet("/finish", async (HttpContext http) =>
        {
            var suspicious = staging.Suspicious();
            if (suspicious.Count > 0 && !http.Request.Query.ContainsKey("anyway"))
            {
                return Results.Text(
                    Page("Something is unaccounted for",
                        string.Join("<br>", suspicious.Select(WebUtility.HtmlEncode))
                        + "<br><br>Nothing has been written. A restart discards everything staged, so "
                        + "either add it to the redact block in capture.local.json, restart and record "
                        + "the affected steps again, or continue with "
                        + "<a href=\"/finish?anyway=1\">/finish?anyway=1</a> if you are sure it is "
                        + "neither personal data nor a token."),
                    "text/html; charset=utf-8");
            }

            var written = await staging.WriteAsync(store, http.RequestAborted);

            // A sitting writes some of the steps and never all of them, so the pack keeps the
            // date it already has. The first sitting finds no manifest and stamps today.
            await store.WriteManifestKeepingDateAsync(http.RequestAborted);

            ReportOcsp(staging, jwks);

            return Results.Text(
                Page("Written", $"{written} exchanges written to {store.Root}."),
                "text/html; charset=utf-8");
        });

        Console.WriteLine("Recording session on http://localhost:5099 - open it in a browser.");
        Console.WriteLine("Finish with http://localhost:5099/finish, which writes the fixtures.");
        await app.RunAsync();
        return 0;
    }

    /// <summary>Which outstanding step a callback belongs to, or null for none.</summary>
    /// <remarks>
    /// <para>
    /// By state wherever there is one. A state that matches nothing outstanding belongs to a step
    /// already consumed - a reload, a resubmitted form - and is not handed to whatever is still
    /// waiting. The second broker's timeout step waits for most of its sitting, and a stray callback
    /// filed under it would have staged another step's exchange there and left the real timeout
    /// nowhere to land.
    /// </para>
    /// <para>
    /// With no state at all, the one outstanding step that expects a code takes it, if there is
    /// exactly one. That fallback is older than the measurement: both brokers have since been seen to
    /// echo a signed step's state, and rehearse checks it on every run.
    /// </para>
    /// </remarks>
    public static string? CallbackStep(string? state, IReadOnlyCollection<ManualCase> outstanding) =>
        state is not null
            ? outstanding.Any(c => c.Id == state) ? state : null
            : outstanding.Where(c => c.ExpectCode).ToList() is [var only] ? only.Id : null;

    /// <summary>
    /// Builds the authorize request for a step. Shared with the rehearsal, so what is checked
    /// beforehand is exactly what the sitting sends.
    /// </summary>
    public static (string Url, string Verifier, string Nonce) BuildAuthorize(
        BrokerTarget broker,
        ManualCase @case,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var verifier = Base64UrlText(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64UrlText(RandomNumberGenerator.GetBytes(16));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = @case.Client.ClientId(),
            ["response_type"] = @case.ResponseType,
            ["redirect_uri"] = @case.RedirectUriOverride ?? RedirectUri,
            ["scope"] = @case.Scope,
            ["state"] = @case.Id,
            ["nonce"] = nonce,
        };

        // Where the first broker's idp_values always sat, so its requests are the bytes they were.
        foreach (var (key, value) in broker.SelectsMitId)
        {
            parameters[key] = value;
        }

        parameters["code_challenge"] = Base64UrlText(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        parameters["code_challenge_method"] = "S256";

        if (@case.ResponseMode is not null)
        {
            parameters["response_mode"] = @case.ResponseMode;
        }

        if (@case.ForcesLogin)
        {
            parameters["prompt"] = "login";
        }

        foreach (var (key, value) in @case.Extra)
        {
            parameters[key] = value;
        }

        // After the step's own Extra, so a rehearsal can turn a step into a variant of itself
        // without a second copy of it drifting out of step with the one the sitting sends.
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[key] = value;
        }

        // Signed last, so the step's own Extra is inside the object rather than beside it -
        // which is the whole point for a step whose idp_params are what it is recording.
        if (@case.SignRequest)
        {
            var signed = RequestObject.Build(
                parameters, @case.Client.ClientId(), broker.Authority, broker.SignerFor(@case.Client));

            parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = @case.Client.ClientId(),
                ["response_type"] = @case.ResponseType,
                ["request"] = signed,
            };
        }

        var url = $"{broker.Authority}/connect/authorize?" + string.Join('&',
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
                ["client_id"] = pending.Case.Client.ClientId(),
                ["client_secret"] = pending.Case.Client.Secret(),
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
                        ["client_id"] = pending.Case.Client.ClientId(),
                        ["client_secret"] = pending.Case.Client.Secret(),
                    }), ct))
                {
                }

                break;

            case FollowUp.CprMatch when accessToken is not null && Target.CprMatchPath is { } cprMatch:
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"{Authority}{cprMatch}"))
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

            // Deliberately none. These would be the browser's request headers, which are not
            // the broker's and belong to nothing StubID reproduces — and the cookie jar among
            // them carried a signed token straight into a fixture, twice.
            [],
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

    private static string Base64UrlText(byte[] bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>
    /// The broker's key set, as it stands today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing key set is not a reason to refuse a sitting. It costs the signature check,
    /// which is recorded as unchecked rather than as failed, and the sitting is worth more
    /// than that one member.
    /// </para>
    /// <para>
    /// Where the broker answers each request with a different subset of its keys, one fetch would
    /// leave a token's kid out often enough to matter. So the set is fetched until three requests
    /// in a row add nothing, and merged. A kid still missing after that is reported as unchecked by
    /// the verifier, never as a failed signature.
    /// </para>
    /// </remarks>
    private static async Task<string?> FetchJwksAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"{Authority}/.well-known/openid-configuration/jwks";
            var keySet = await client.GetStringAsync(url);
            var count = TokenFixtures.KeyCount(keySet);

            for (var (requests, quiet) = (1, 0); Target.KeySetVariesPerRequest && quiet < 3 && requests < 20; requests++)
            {
                try
                {
                    var merged = TokenFixtures.MergeKeySets([keySet, await client.GetStringAsync(url)]);
                    var mergedCount = TokenFixtures.KeyCount(merged);
                    quiet = mergedCount == count ? quiet + 1 : 0;
                    (keySet, count) = (merged, mergedCount);
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                                                  or JsonException or KeyNotFoundException
                                                  or InvalidOperationException)
                {
                    // The keys already in hand are worth more than a complete set nobody got.
                    Console.Error.WriteLine(
                        $"  a further key set request failed, keeping the {count} keys fetched so far: {error.Message}");
                    break;
                }
            }

            return keySet;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                                          or JsonException or KeyNotFoundException
                                          or InvalidOperationException)
        {
            Console.Error.WriteLine($"  could not fetch the key set: {error.Message}");
            return null;
        }
    }

    private static string Launchpad(IReadOnlyList<ManualCase> cases, Staging staging)
    {
        var rows = string.Join("\n", cases.Select(c =>
        {
            var done = staging.Recorded.Contains(c.Id) ? "recorded" : "";
            var session = c.ForcesLogin ? "re-authenticates" : "rides the session";
            return $"""
                <tr>
                  <td><a href="/start/{c.Id}">{c.Id}</a></td>
                  <td>{WebUtility.HtmlEncode(c.Step)}</td>
                  <td>{WebUtility.HtmlEncode(c.Title)}</td>
                  <td>{WebUtility.HtmlEncode(c.Operator)}</td>
                  <td>{session}</td>
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
            <tr><th>Case</th><th>Step</th><th>What it records</th><th>What you do</th>
            <th>MitID</th><th></th></tr>
            {rows}
            </table>
            <p>Every step but the single sign-on one forces a fresh authentication, so you
            reach the authenticator each time rather than being waved through by a session left
            behind by the previous step.</p>
            <p><a href="/staged">See what has been captured</a> ({staging.Count} exchanges staged)
            &middot; <a href="/logout">End the broker session</a>
            &middot; <a href="/finish">Finish and write the fixtures</a></p>
            """);
    }

    /// <summary>
    /// Says what the OCSP responses in this sitting answered, while the sitting is still open.
    /// </summary>
    /// <remarks>
    /// The runbook's step 10 asked for this and got it by hand. What the recordings say is
    /// asserted on every build by OcspResponseContractTests, which is the half that lasts; this
    /// is so the operator reads the answer in the chair rather than a week later. It prints and
    /// never throws, because nothing here is worth ending a sitting over.
    /// </remarks>
    private static void ReportOcsp(Staging staging, string? jwks)
    {
        foreach (var (@case, exchange, _, body) in staging.Preview())
        {
            if (!body.TrimStart().StartsWith('{'))
            {
                continue;
            }

            string? served;
            string? tokenKid;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("transaction_token_ocsp_resp", out var member))
                {
                    continue;
                }

                served = member.GetString();
                tokenKid = json.RootElement.TryGetProperty("transaction_token", out var token)
                    ? KidOf(token.GetString())
                    : null;
            }
            catch (JsonException)
            {
                continue;
            }

            var response = served is null ? null : Ocsp.Describe(served);
            if (response is null)
            {
                Console.WriteLine($"  {@case}/{exchange}: an OCSP response that did not parse. "
                    + "Keep the bytes and write down that it did not.");
                continue;
            }

            var single = response.Responses.FirstOrDefault();
            Console.WriteLine($"  {@case}/{exchange}: OCSP {single?.CertStatus ?? "with no answer in it"}"
                + $", produced at {response.ProducedAt:O}, {NamesTheSigningKey(single, tokenKid, jwks)}");
        }
    }

    private static string? KidOf(string? compact)
    {
        var parts = compact?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var header = JsonDocument.Parse(
                System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]));

            return header.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the answer is about the certificate that signed the token it arrived with.
    /// </summary>
    private static string NamesTheSigningKey(
        OcspSingleResponse? single, string? kid, string? jwks)
    {
        if (single is null || kid is null || jwks is null)
        {
            return "nothing to match it against";
        }

        try
        {
            using var keys = JsonDocument.Parse(jwks);
            var key = keys.RootElement.GetProperty("keys").EnumerateArray()
                .FirstOrDefault(k => k.TryGetProperty("kid", out var candidate)
                                     && candidate.GetString() == kid);

            if (key.ValueKind != JsonValueKind.Object
                || !key.TryGetProperty("x5c", out var chain)
                || chain.GetArrayLength() == 0)
            {
                return $"kid {kid} resolves to no certificate in today's key set";
            }

            using var certificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(chain[0].GetString()!));

            return Ocsp.Matches(single, certificate)
                ? $"and it names {certificate.Subject}"
                : $"but it does NOT name {certificate.Subject} - write that down";
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException)
        {
            return "and the key set could not be read to match it";
        }
    }

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{font:14px system-ui;margin:2rem;max-width:60rem}td,th{text-align:left}</style>
        </head><body><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</body></html>
        """;
}
