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
        // ASP.NET routing matches case-insensitively and forgives a trailing slash. The
        // broker forgives neither, and a client that reaches metadata here but 404s against
        // pre-production is the false pass this project exists to prevent.
        app.MapGet("/op/.well-known/openid-configuration", (HttpContext http, Documents documents) =>
            Exactly(http, "/op/.well-known/openid-configuration")
                ? Json(documents.Discovery(BaseUrl(http)))
                : Results.NotFound());

        app.MapGet("/op/.well-known/openid-configuration/jwks", (HttpContext http, Keys keys) =>
            Exactly(http, "/op/.well-known/openid-configuration/jwks")
                ? Json(keys.Ring.ToJwks())
                : Results.NotFound());

        app.MapPost("/op/connect/par", async (HttpContext http, BrokerState state) =>
        {
            var form = await http.Request.ReadFormAsync();
            var (parClientId, parSecret) = ClientCredentials(http, form);

            // CAP-019: an unauthenticated push is refused the same way the token endpoint
            // refuses one, and by the same rule.
            if (!state.IsKnownClient(parClientId) || string.IsNullOrEmpty(parSecret))
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

            // A client may only ask for what it is registered for, which is how the broker
            // refuses a code client that asks for an id_token.
            if (!state.Allows(request.ClientId, request.ResponseType))
            {
                return ErrorPage(http, protection, "unauthorized_client",
                    $"Response type '{request.ResponseType}' is not enabled for this client.");
            }

            // The slice approves immediately. Parking the request for a decision is the next
            // milestone; the shape of what comes back does not change.
            var issued = state.IssueCode(request, state.DefaultCitizen, clock.GetUtcNow());
            var wants = request.ResponseType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var organisation = state.OrganisationOf(request.ClientId);

            // Member order as recorded: the code first, then a front-channel id_token if one
            // was asked for, then state and session_state.
            var response = new Dictionary<string, string>();

            if (wants.Contains("code"))
            {
                response["code"] = issued;
            }

            if (wants.Contains("id_token"))
            {
                // The front-channel token covers the code with c_hash rather than an access
                // token with at_hash: there is no access token in the front channel.
                response["id_token"] = tokens.IdToken(
                    Issuer(http),
                    state.PeekCode(issued)!,
                    accessToken: null,
                    organisation,
                    authorizationCode: wants.Contains("code") ? issued : null);
            }

            if (request.State is not null)
            {
                response["state"] = request.State;
            }

            response["session_state"] = SessionState(request.ClientId, issued);

            // Advertised in discovery, so a client may enforce it - but the broker omits it
            // whenever an id_token is returned, which already carries the issuer.
            if (!wants.Contains("id_token"))
            {
                response["iss"] = Issuer(http);
            }

            // A response carrying an id_token defaults to form_post, since a token in a query
            // string ends up in logs and history.
            if (request.ResponseMode == "query" && wants.Contains("id_token"))
            {
                request = request with { ResponseMode = "form_post" };
            }

            return request.ResponseMode switch
            {
                "form_post" => FormPost(request.RedirectUri, response),
                "query" => Results.Redirect(Append(request.RedirectUri, response, '?')),
                "fragment" => Results.Redirect(Append(request.RedirectUri, response, '#')),
                _ => ErrorPage(http, protection, "invalid_request",
                    $"Response mode '{request.ResponseMode}' is not supported."),
            };
        });

        app.MapPost("/op/connect/token", async (HttpContext http, BrokerState state, Tokens tokens, TimeProvider clock) =>
        {
            var form = await http.Request.ReadFormAsync();
            var (clientId, secret) = ClientCredentials(http, form);

            if (!state.IsKnownClient(clientId) || string.IsNullOrEmpty(secret))
            {
                return OAuthError(http, "invalid_client");
            }

            var grantType = form["grant_type"].ToString();
            if (grantType == "client_credentials")
            {
                // CAP-016: the broker answers unauthorized_client rather than complaining
                // about the grant, because the grant exists and the client may not use it.
                return OAuthError(http, "unauthorized_client");
            }

            if (grantType != "authorization_code")
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

            // Member order as recorded, and at_hash covers the access token, so the id_token
            // is composed after it exists.
            return Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id_token"] = tokens.IdToken(
                    Issuer(http), issued, accessToken, state.OrganisationOf(clientId!)),
                ["access_token"] = accessToken,
                ["expires_in"] = Tokens.AccessTokenLifetimeSeconds,
                ["token_type"] = "Bearer",
                ["scope"] = issued.Request.Scope,
                ["userinfo_token"] = tokens.UserInfoToken(
                    Issuer(http), issued, state.OrganisationOf(clientId!)),
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
                foreach (var claim in tokens.UserInfo(state.OrganisationOf(issued.ClientId), issued))
                {
                    json.WritePropertyName(claim.Name);
                    using var value = JsonDocument.Parse(claim.RawJson);
                    value.RootElement.WriteTo(json);
                }

                json.WriteEndObject();
            }

            return Json(Encoding.UTF8.GetString(buffer.ToArray()));
        });

        app.MapPost("/op/api/v1/mitid/matchCpr", (HttpContext http) =>
        {
            // CAP-018: a bare challenge, unlike userinfo's. Two endpoints on one host with two
            // different WWW-Authenticate strings is exactly what a generated emulator smooths
            // over. Matching a CPR needs a session, which arrives with the approval engine.
            http.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Empty;
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
            // A malformed header is a bad request, not a crash. Convert.FromBase64String
            // throws on anything that is not base64, which answered 500 with an empty body.
            Span<byte> credentials = new byte[header.Length];
            if (Convert.TryFromBase64String(header[6..], credentials, out var written))
            {
                var decoded = Encoding.UTF8.GetString(credentials[..written]);
                var separator = decoded.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    return (Uri.UnescapeDataString(decoded[..separator]),
                            Uri.UnescapeDataString(decoded[(separator + 1)..]));
                }
            }

            return (null, null);
        }

        return (Optional(form.ToDictionary(f => f.Key, f => f.Value.ToString()), "client_id"),
                Optional(form.ToDictionary(f => f.Key, f => f.Value.ToString()), "client_secret"));
    }

    /// <summary>
    /// The session-management parameter every recorded callback carries: an opaque value
    /// followed by a salt, separated by a dot.
    /// </summary>
    private static string SessionState(string clientId, string code)
    {
        var salt = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code))[..16]);
        var value = Base64Url.Encode(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}{code}{salt}")));

        return $"{value}.{salt}";
    }

    /// <summary>Whether the path arrived exactly as written, case and trailing slash included.</summary>
    private static bool Exactly(HttpContext http, string path) =>
        string.Equals(http.Request.Path.Value, path, StringComparison.Ordinal);

    private static string BaseUrl(HttpContext http) =>
        http.RequestServices.GetRequiredService<IConfiguration>()["StubId:PublicBaseUrl"]
        ?? $"{http.Request.Scheme}://{http.Request.Host}";

    private static string Issuer(HttpContext http) => $"{BaseUrl(http)}/op";

    /// <summary>
    /// The charset is part of what the recordings carry, and the uppercase spelling is the
    /// broker's. Passing the literal keeps it; the encoding overload would lowercase it.
    /// </summary>
    private static IResult Json(string body) => Results.Text(body, "application/json; charset=UTF-8");

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

    private static string Append(string redirectUri, IDictionary<string, string> values, char mode)
    {
        var separator = mode == '#'
            ? '#'
            : redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        var pairs = string.Join('&', values.Select(v =>
            $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));

        return $"{redirectUri}{separator}{pairs}";
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
