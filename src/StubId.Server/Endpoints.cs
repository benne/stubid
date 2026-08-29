using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StubId.Wire;

namespace StubId.Server;

public static class Endpoints
{
    /// <summary>The recorded challenge, byte for byte. Note the absent space after the comma.</summary>
    private const string BearerChallenge = "Bearer realm=\"IdentityServer\",error=\"invalid_token\"";

    public static void MapBroker(this WebApplication app)
    {
        app.MapGet("/op/.well-known/openid-configuration", (HttpContext http, Documents documents) =>
            Json(documents.Discovery(BaseUrl(http))));

        app.MapGet("/op/.well-known/openid-configuration/jwks", (Keys keys) => Json(keys.Ring.ToJwks()));

        app.MapPost("/op/connect/par", async (HttpContext http, BrokerState state) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (!state.IsKnownClient(form["client_id"]))
            {
                return OAuthError(http, "invalid_client");
            }

            var requestUri = state.PushRequest(Parse(form.ToDictionary(f => f.Key, f => f.Value.ToString())));

            // 201, and the reference expires in ten minutes.
            http.Response.StatusCode = (int)HttpStatusCode.Created;
            return Json(JsonSerializer.Serialize(new { request_uri = requestUri, expires_in = 600 }));
        });

        app.MapMethods("/op/connect/authorize", ["GET", "POST"], async (
            HttpContext http, BrokerState state, Tokens tokens, TimeProvider clock, IDataProtectionProvider protection) =>
        {
            var parameters = await ReadParameters(http);

            // A pushed request carries everything; the redirect then names only the client
            // and the reference.
            AuthorizationRequest? request = null;
            if (parameters.TryGetValue("request_uri", out var requestUri))
            {
                request = state.RedeemPushedRequest(requestUri);
                if (request is null)
                {
                    return ErrorPage(http, protection, "invalid_request", "Unknown or expired request_uri.");
                }
            }

            request ??= Parse(parameters);

            // An invalid request is never redirected back to the client: the broker shows its
            // own page instead, so the client sees nothing at all.
            if (!state.IsKnownClient(request.ClientId))
            {
                return ErrorPage(http, protection, "unauthorized_client", "Unknown client or client not enabled.");
            }

            if (string.IsNullOrEmpty(request.RedirectUri))
            {
                return ErrorPage(http, protection, "invalid_request", "Invalid redirect_uri.");
            }

            if (!request.ResponseType.Split(' ').Contains("code"))
            {
                return ErrorPage(http, protection, "unsupported_response_type",
                    $"Response type '{request.ResponseType}' is not supported yet.");
            }

            // The slice approves immediately. Parking the request for a decision is the next
            // milestone; the shape of what comes back does not change.
            var code = state.IssueCode(request, state.DefaultCitizen, clock.GetUtcNow());

            var response = new Dictionary<string, string> { ["code"] = code };
            if (request.State is not null)
            {
                response["state"] = request.State;
            }

            // Advertised in the discovery document, so a client may enforce its presence.
            response["iss"] = Issuer(http);

            return request.ResponseMode == "form_post"
                ? FormPost(request.RedirectUri, response)
                : Results.Redirect(QueryHelpers(request.RedirectUri, response));
        });

        app.MapPost("/op/connect/token", async (HttpContext http, BrokerState state, Tokens tokens, TimeProvider clock) =>
        {
            var form = await http.Request.ReadFormAsync();
            var (clientId, secret) = ClientCredentials(http, form);

            if (!state.IsKnownClient(clientId) || string.IsNullOrEmpty(secret))
            {
                return OAuthError(http, "invalid_client");
            }

            if (form["grant_type"] != "authorization_code")
            {
                return OAuthError(http, "unsupported_grant_type");
            }

            var issued = state.RedeemCode(form["code"].ToString());
            if (issued is null || issued.Request.ClientId != clientId)
            {
                return OAuthError(http, "invalid_grant");
            }

            if (issued.Request.RedirectUri != form["redirect_uri"].ToString())
            {
                return OAuthError(http, "invalid_grant");
            }

            if (issued.Request.CodeChallenge is { } challenge
                && !Pkce.Verify(form["code_verifier"].ToString(), challenge,
                    issued.Request.CodeChallengeMethod ?? "plain"))
            {
                return OAuthError(http, "invalid_grant");
            }

            var accessToken = state.IssueAccessToken(issued);

            return Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id_token"] = tokens.IdToken(Issuer(http), issued),
                ["access_token"] = accessToken,
                ["expires_in"] = Tokens.AccessTokenLifetimeSeconds,
                ["token_type"] = "Bearer",
                ["scope"] = issued.Request.Scope,
            }));
        });

        app.MapMethods("/op/connect/userinfo", ["GET", "POST"], (HttpContext http, BrokerState state, Tokens tokens) =>
        {
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
            var issued = token is null ? null : state.ReadAccessToken(token);

            if (issued is null)
            {
                // 401 with an empty body, and the challenge byte for byte.
                http.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                http.Response.Headers.WWWAuthenticate = BearerChallenge;
                return Results.Empty;
            }

            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                foreach (var claim in tokens.UserInfo(ClientOf(issued, state), issued))
                {
                    json.WritePropertyName(claim.Name);
                    using var value = JsonDocument.Parse(claim.RawJson);
                    value.RootElement.WriteTo(json);
                }

                json.WriteEndObject();
            }

            return Json(Encoding.UTF8.GetString(buffer.ToArray()));
        });

        app.MapGet("/op/Error", (HttpContext http, IDataProtectionProvider protection) =>
        {
            var errorId = http.Request.Query["errorId"].ToString();
            var (code, description) = Unprotect(protection, errorId);

            // StubID's own page. The broker's wording is its own to publish.
            return Results.Text($"""
                <!DOCTYPE html>
                <html lang="en"><head><meta charset="utf-8"><title>StubID</title></head>
                <body>
                <h1>The request was refused</h1>
                <p>Fejlkode: {WebUtility.HtmlEncode(code)}</p>
                <p>{WebUtility.HtmlEncode(description)}</p>
                <p>This is StubID, an emulator. No authentication took place.</p>
                </body></html>
                """, "text/html; charset=utf-8");
        });
    }

    private static string ClientOf(IssuedAccessToken token, BrokerState state) =>
        state.Clients.Keys.First();

    private static async Task<Dictionary<string, string>> ReadParameters(HttpContext http)
    {
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            return form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);
        }

        return http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
    }

    private static AuthorizationRequest Parse(IDictionary<string, string> p) => new(
        ClientId: Value(p, "client_id"),
        RedirectUri: Value(p, "redirect_uri"),
        ResponseType: Value(p, "response_type"),
        ResponseMode: p.TryGetValue("response_mode", out var mode) && mode.Length > 0 ? mode : "query",
        Scope: Value(p, "scope"),
        State: Optional(p, "state"),
        Nonce: Optional(p, "nonce"),
        CodeChallenge: Optional(p, "code_challenge"),
        CodeChallengeMethod: Optional(p, "code_challenge_method"));

    private static string Value(IDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var value) ? value : "";

    private static string? Optional(IDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static (string? ClientId, string? Secret) ClientCredentials(HttpContext http, IFormCollection form)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                return (Uri.UnescapeDataString(decoded[..separator]), Uri.UnescapeDataString(decoded[(separator + 1)..]));
            }
        }

        return (Optional(form.ToDictionary(f => f.Key, f => f.Value.ToString()), "client_id"),
                Optional(form.ToDictionary(f => f.Key, f => f.Value.ToString()), "client_secret"));
    }

    private static string BaseUrl(HttpContext http) =>
        http.RequestServices.GetRequiredService<IConfiguration>()["StubId:PublicBaseUrl"]
        ?? $"{http.Request.Scheme}://{http.Request.Host}";

    private static string Issuer(HttpContext http) => $"{BaseUrl(http)}/op";

    private static IResult Json(string body) => Results.Text(body, "application/json");

    /// <summary>A bare error object: no description, no uri, exactly as recorded.</summary>
    private static IResult OAuthError(HttpContext http, string error)
    {
        http.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        return Json($"{{\"error\":\"{error}\"}}");
    }

    private static IResult ErrorPage(HttpContext http, IDataProtectionProvider protection, string code, string description)
    {
        // A real protected payload, so it carries the same prefix and round-trips.
        var protector = protection.CreateProtector("StubId.ErrorPage");
        var errorId = protector.Protect($"{code}|{description}");

        return Results.Redirect($"{BaseUrl(http)}/op/Error?errorId={Uri.EscapeDataString(errorId)}");
    }

    private static (string Code, string Description) Unprotect(IDataProtectionProvider protection, string errorId)
    {
        try
        {
            var parts = protection.CreateProtector("StubId.ErrorPage").Unprotect(errorId).Split('|', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return ("invalid_request", "The error reference is not valid.");
        }
    }

    private static string QueryHelpers(string redirectUri, IDictionary<string, string> values)
    {
        var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var query = string.Join('&', values.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));
        return $"{redirectUri}{separator}{query}";
    }

    /// <summary>
    /// The self-submitting form ASP.NET Core asks for by default, hand-rendered so the values
    /// are encoded once and exactly.
    /// </summary>
    private static IResult FormPost(string redirectUri, IDictionary<string, string> values)
    {
        var fields = string.Join("\n", values.Select(v =>
            $"""<input type="hidden" name="{WebUtility.HtmlEncode(v.Key)}" value="{WebUtility.HtmlEncode(v.Value)}" />"""));

        return Results.Text($"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Working...</title></head>
            <body onload="document.forms[0].submit()">
            <form method="post" action="{WebUtility.HtmlEncode(redirectUri)}">
            {fields}
            <noscript><button type="submit">Continue</button></noscript>
            </form>
            </body></html>
            """, "text/html; charset=utf-8");
    }
}
