using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StubId.Abstractions;
using StubId.Profiles;
using StubId.Server.Sessions;
using StubId.Wire;

namespace StubId.Server;

public static class Endpoints
{
    /// <summary>The recorded challenge, byte for byte. Note the absent space after the comma.</summary>
    private const string BearerChallenge = "Bearer realm=\"IdentityServer\",error=\"invalid_token\"";

    /// <summary>
    /// The Nets eID Broker route table. Patterns are relative to the tenant root, so the host
    /// composes any mount prefix and the same declarations serve a tenant at a subdomain or
    /// under a path.
    /// </summary>
    public static IReadOnlyList<RouteDeclaration> Declare()
    {
        var routes = new List<RouteDeclaration>();

        void Map(string pattern, string[] methods, RouteRole role, Delegate handler) =>
            routes.Add(new RouteDeclaration(pattern, methods, role, handler)
            {
                // Probed against pre-production: the first segment is compared ordinally
                // because a proxy selects the application by it, everything below it is not,
                // and a trailing slash is refused.
                Exactness = SegmentExactness.FirstOrdinalThenInsensitive(TrailingSlash.Refuse),
            });

        Map("op/.well-known/openid-configuration", ["GET"], RouteRole.Discovery, (HttpContext http, Documents documents) =>
            Json(documents.Discovery(BaseUrl(http))));

        Map("op/.well-known/openid-configuration/jwks", ["GET"], RouteRole.Jwks, (HttpContext http, Keys keys) =>
            Json(keys.Ring.ToJwks()));

        Map("op/connect/par", ["POST"], RouteRole.Par, async (HttpContext http, BrokerState state) =>
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

        Map("op/connect/authorize", ["GET", "POST"], RouteRole.Authorize, async (
            HttpContext http, BrokerState state, Tokens tokens, TimeProvider clock,
            IDataProtectionProvider protection, SessionStore sessions, Citizens citizens) =>
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

            // The broker's own parameters. Which of these are refused here and which are
            // carried through is recorded rather than reasoned about.
            if (RequestGrammar.Fault(request, parameters) is var (code, description))
            {
                return ErrorPage(http, protection, code, description);
            }

            // The request is parked and the ladder is asked what should happen to it. Most
            // logins are decided by something already in place and never wait at all; the ones
            // that do are what the control API and the login page are for.
            var session = sessions.Park(request.ClientId, http.Request.QueryString.Value ?? "",
                new SessionContext(
                    SessionId: "",
                    ClientId: request.ClientId,
                    Scope: request.Scope,
                    Parameters: parameters,
                    Now: clock.GetUtcNow()));

            if (!session.IsDecided)
            {
                // prompt=none says: answer without asking the user anything. Nothing had an
                // opinion, so answering would mean asking, and the specification's word for
                // that is login_required. Unrecorded - reaching it against the broker needs a
                // client with single sign-on and a session already open.
                if (Prompts(parameters).Contains("none"))
                {
                    sessions.Decide(
                        session.Id,
                        Decision.Refused(SilentLoginImpossible, SilentLoginImpossible),
                        "prompt=none, and nothing could answer without asking");

                    return Refuse(http, request, session);
                }

                // Nobody has an opinion yet, so the browser waits where a person can act on it.
                return Results.Redirect($"{BaseUrl(http)}/op/Login?session={session.Id}");
            }

            if (session.State is SessionState.Failed or SessionState.Expired)
            {
                // A user-level failure is reported back to the client, unlike an invalid
                // request, which never reaches it.
                return Refuse(http, request, session);
            }

            var citizen = citizens.ById(session.CitizenId!)
                ?? throw new InvalidOperationException($"No citizen {session.CitizenId}.");

            var issued = state.IssueCode(request, citizen, clock.GetUtcNow(), ClientIp(http));
            session.TryRedeem();
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

            response["session_state"] = SessionStateParameter(request.ClientId, issued);

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

        Map("op/connect/token", ["POST"], RouteRole.Token, async (HttpContext http, BrokerState state, Tokens tokens, TimeProvider clock) =>
        {
            var form = await http.Request.ReadFormAsync();

            // CAP-042: nothing at all is a bad request, not a failed authentication. Both are
            // true of an empty POST, and only the recording says which the broker reports.
            if (form.Count == 0 && http.Request.Headers.Authorization.Count == 0)
            {
                return OAuthError(http, "invalid_request");
            }

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
            var body = new Dictionary<string, object>
            {
                ["id_token"] = tokens.IdToken(
                    Issuer(http), issued, accessToken, state.OrganisationOf(clientId!)),
                ["access_token"] = accessToken,
                ["expires_in"] = Tokens.AccessTokenLifetimeSeconds,
                ["token_type"] = "Bearer",
                ["scope"] = issued.Request.Scope,
                ["userinfo_token"] = tokens.UserInfoToken(
                    Issuer(http), issued, state.OrganisationOf(clientId!)),
            };

            // Last, and both or neither: no recorded body carries one without the other.
            if (issued.Request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("transaction_token"))
            {
                body["transaction_token"] = tokens.TransactionToken(
                    Issuer(http), issued, state.OrganisationOf(clientId!));
                body["transaction_token_ocsp_resp"] = tokens.TransactionTokenOcspResponse();
            }

            return Json(JsonSerializer.Serialize(body));
        });

        Map("op/connect/userinfo", ["GET", "POST"], RouteRole.UserInfo, (HttpContext http, BrokerState state, Tokens tokens) =>
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

        // How a private service provider checks a personal number it already holds, since it
        // may not ask for one. Three attempts to a session, which is behaviour rather than
        // configuration: a suite that passes here and fails on the fourth call against the
        // broker has been told nothing useful.
        Map("op/api/v1/mitid/matchCpr", ["POST"], RouteRole.Extra("matchCpr"), async (
            HttpContext http, BrokerState state, CprMatch attempts) =>
        {
            var issued = Bearer(http, state);

            if (issued is null)
            {
                // CAP-018: a bare challenge, unlike userinfo's. Two endpoints on one host with
                // two different WWW-Authenticate strings is exactly what a generated emulator
                // smooths over.
                http.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Empty;
            }

            var submitted = await Submitted(http, "cpr");

            if (string.IsNullOrEmpty(submitted))
            {
                // CAP-021: its own envelope, not an OAuth error. A different endpoint family
                // on the same host, and it answers in a different shape.
                http.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return Json("""{"errorMessage":"Missing Cpr parameter"}""");
            }

            if (!attempts.TryAttempt(issued.SessionId))
            {
                http.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return Json(JsonSerializer.Serialize(new { errorMessage = CprMatch.Exceeded }));
            }

            return Json(Matched(
                submitted.Replace("-", "", StringComparison.Ordinal) == issued.Citizen.Cpr));
        });

        // The broker's own way to end a session from the back channel, which its documentation
        // recommends over sending the browser to end_session.
        Map("op/api/v1/session/logout", ["POST"], RouteRole.Extra("sessionLogout"), (
            HttpContext http, BrokerState state, CprMatch attempts) =>
        {
            var issued = Bearer(http, state);

            if (issued is null)
            {
                http.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Empty;
            }

            state.EndSession(issued.SessionId);
            attempts.Forget(issued.SessionId);

            return Results.NoContent();
        });

        // Where a parked login waits. Plainly StubID's own page: reproducing the broker's
        // authenticator would put someone else's trade dress on an emulator, and a page that
        // looked real is a page someone can be fooled by.
        Map("op/Login", ["GET", "POST"], RouteRole.Extra("login"), async (
            HttpContext http, SessionStore sessions, Citizens citizens) =>
        {
            var id = http.Request.Query["session"].ToString();
            var session = sessions.Find(id);

            if (session is null)
            {
                return Results.NotFound();
            }

            if (HttpMethods.IsPost(http.Request.Method))
            {
                var form = await http.Request.ReadFormAsync();
                var chosen = citizens.ById(form["citizen"].ToString()) ?? citizens.Default;

                var decision = form["decision"] == "approve"
                    ? chosen?.Outcome() ?? Decision.Refused("mitid_identity_not_found")
                    : Decision.Refused("mitid_user_aborted");

                var decided = sessions.Decide(id, decision, "the login page");

                return Results.Text(
                    Page(decided ? "Decided" : "Already decided",
                        decided
                            ? $"<p>This login is now {sessions.Find(id)?.State}. Return to the application.</p>"
                            : "<p>Something decided this login first. Nothing was changed.</p>"),
                    "text/html; charset=utf-8");
            }

            if (session.IsDecided)
            {
                return Results.Text(
                    Page("Already decided", $"<p>This login is {session.State}.</p>"),
                    "text/html; charset=utf-8");
            }

            var options = string.Join("\n", citizens.All.OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => $"""<option value="{WebUtility.HtmlEncode(c.Id)}">{WebUtility.HtmlEncode(c.Name)}</option>"""));

            return Results.Text(Page("StubID", $"""
                <p>This is StubID, an emulator.</p>
                <p><strong>No identity is being verified, and no real authentication is taking place.</strong></p>
                <form method="post">
                  <p><label>Sign in as <select name="citizen">{options}</select></label></p>
                  <p>
                    <button type="submit" name="decision" value="approve">Approve</button>
                    <button type="submit" name="decision" value="reject">Abort</button>
                  </p>
                </form>
                <p>A test can do the same through the control API without a browser.</p>
                """), "text/html; charset=utf-8");
        });

        // CAP-044 and CAP-045: without a usable id_token_hint the broker goes to its own
        // logout page and ignores post_logout_redirect_uri entirely. A client that omits the
        // hint never comes back, and this is where that starts.
        Map("op/connect/endsession", ["GET", "POST"], RouteRole.Extra("endsession"), async (
            HttpContext http, BrokerState state) =>
        {
            var parameters = await ReadParameters(http);

            return EndSession(parameters, state) is { } destination
                ? Results.Redirect(destination)
                : Results.Redirect($"{BaseUrl(http)}/op/Account/Logout");
        });

        Map("op/Account/Logout", ["GET"], RouteRole.Extra("logout"), () => Results.Text(
            Page("Signed out", "<p>The session is over. This is StubID, an emulator.</p>"),
            "text/html; charset=utf-8"));

        Map("op/Error", ["GET"], RouteRole.ErrorPage, (HttpContext http, IDataProtectionProvider protection) =>
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

        return routes;
    }

    /// <summary>
    /// Sends a user-level failure back to the client, carrying the broker's own error code in
    /// error_description rather than a description of it.
    /// </summary>
    private static IResult Refuse(HttpContext http, AuthorizationRequest request, AuthSession session)
    {
        var response = new Dictionary<string, string>
        {
            ["error"] = session.OAuthError ?? "access_denied",
            ["error_description"] = session.ErrorCode ?? "mitid_unexpected_error",
        };

        if (request.State is not null)
        {
            response["state"] = request.State;
        }

        // CAP-023: a failed login carries session_state like a successful one, and carries no
        // iss, even though discovery advertises support for it. The success path omits iss
        // only when an id_token is returned; a failure omits it either way.
        response["session_state"] = SessionStateParameter(request.ClientId, session.Id);

        return request.ResponseMode == "form_post"
            ? FormPost(request.RedirectUri, response)
            : Results.Redirect(Append(request.RedirectUri, response, '?'));
    }

    /// <summary>
    /// The answer to a CPR match. A JSON boolean, which is what the pre-production swagger
    /// declares.
    /// </summary>
    /// <remarks>
    /// Unrecorded, and worth doubting rather than assuming: everything on the userinfo side of
    /// this broker is a JSON string, including two values that are plainly booleans, and the
    /// vendor's own claim tables have already been wrong about typing once. The sitting that
    /// would have settled it spent its three attempts on the other branches, so this is the
    /// documented shape until a recording says otherwise.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.DocsConfirmed,
        Evidence = "The pre-production swagger. Unrecorded: no capture reached a successful match.")]
    private static string Matched(bool matches) =>
        JsonSerializer.Serialize(new { cprNumberMatch = matches });

    /// <summary>
    /// Where end session sends the browser, or null for the broker's own logout page.
    /// </summary>
    /// <remarks>
    /// Two halves with two different provenances. Without a usable hint the redirect is
    /// ignored outright, which is recorded twice; honouring it with one is documented rather
    /// than recorded, because reaching that branch needs a real id_token and so a real login.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-044, fixtures/neb/pp/CAP-045")]
    private static string? EndSession(IDictionary<string, string> parameters, BrokerState state)
    {
        var hint = Optional(parameters, "id_token_hint");
        var wanted = Optional(parameters, "post_logout_redirect_uri");

        if (hint is null || wanted is null || !state.EndsSession(hint))
        {
            return null;
        }

        return Optional(parameters, "state") is { } echoed
            ? Append(wanted, new Dictionary<string, string> { ["state"] = echoed }, '?')
            : wanted;
    }

    /// <summary>
    /// The answer to a silent login nothing could resolve. Unrecorded: reaching it against the
    /// broker needs a client with single sign-on and a session already open, so this is the
    /// specification's answer rather than the broker's observed one.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.DocsConfirmed,
        Evidence = "OpenID Connect Core 3.1.2.6. Unrecorded: needs an established SSO session.")]
    private const string SilentLoginImpossible = "login_required";

    /// <summary>
    /// A space-delimited list, as the specification spells it and as discovery advertises:
    /// login, none and select_account.
    /// </summary>
    private static IReadOnlyList<string> Prompts(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("prompt", out var value)
            ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>The access token behind a call, or null if there is not a usable one.</summary>
    private static IssuedAccessToken? Bearer(HttpContext http, BrokerState state)
    {
        var header = http.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? state.ReadAccessToken(header[7..])
            : null;
    }

    /// <summary>
    /// One member of a submitted body, whether it arrived as JSON or as a form. The broker's
    /// own API takes JSON; a form is what a hand-written test tends to send, and accepting
    /// both costs nothing.
    /// </summary>
    private static async Task<string?> Submitted(HttpContext http, string name)
    {
        if (http.Request.HasFormContentType)
        {
            return Optional(
                (await http.Request.ReadFormAsync()).ToDictionary(f => f.Key, f => f.Value.ToString()),
                name);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// Where the browser authorizing came from, for the transaction token's
    /// <c>transaction_client_ip</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unmapped first. Every listener StubID ships is dual-stack — the image sets
    /// <c>ASPNETCORE_URLS=http://+:8080</c> — so an IPv4 client arrives as an IPv4-mapped IPv6
    /// address and renders as <c>::ffff:10.5.0.2</c>. All three recordings carry a dotted quad,
    /// and a client that parses this claim as IPv4 fails outright on the mapped form.
    /// </para>
    /// <para>
    /// The in-memory test host populates no remote address at all, so this is null on the path
    /// nearly every test in this repository drives. Loopback is the honest answer for a request
    /// that never crossed a network, and it is written the same way a real one now is.
    /// </para>
    /// </remarks>
    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress switch
        {
            null => "127.0.0.1",
            { IsIPv4MappedToIPv6: true } mapped => mapped.MapToIPv4().ToString(),
            var address => address.ToString(),
        };

    /// <summary>
    /// Narrows a request to what is carried past the endpoint that received it.
    /// </summary>
    /// <remarks>
    /// idp_params is decoded here rather than where the session is created, and the placement is
    /// load-bearing. The PAR handler calls this and returns without reaching the session at all,
    /// so a push would otherwise arrive at the token endpoint with its parameters already thrown
    /// away - and nothing downstream of a push has anything but this record to read.
    /// </remarks>
    private static AuthorizationRequest Parse(IDictionary<string, string> p) => new(
        ClientId: Value(p, "client_id"),
        RedirectUri: Value(p, "redirect_uri"),
        ResponseType: Value(p, "response_type"),
        ResponseMode: p.TryGetValue("response_mode", out var mode) && mode.Length > 0 ? mode : "query",
        Scope: Value(p, "scope"),
        State: Optional(p, "state"),
        Nonce: Optional(p, "nonce"),
        CodeChallenge: Optional(p, "code_challenge"),
        CodeChallengeMethod: Optional(p, "code_challenge_method"),
        MitIdParameters: RequestGrammar.IdentityProviderParameters(
            p.AsReadOnly(), "mitid"));

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
    private static string SessionStateParameter(string clientId, string code)
    {
        var salt = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code))[..16]);
        var value = Base64Url.Encode(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}{code}{salt}")));

        return $"{value}.{salt}";
    }

    /// <summary>
    /// The address this instance was told to answer at, never the one the request arrived on.
    /// </summary>
    /// <remarks>
    /// Deriving this from the Host header would make the issuer right for whoever asked and
    /// wrong for everybody else: a browser reaching a container on a mapped port and an
    /// application reaching it by service name would discover two different issuers from one
    /// instance, and every client library compares the issuer it discovers against the authority
    /// it was configured with character for character. An instance that has not been told its
    /// own address refuses rather than guessing one.
    /// </remarks>
    private static string BaseUrl(HttpContext http) =>
        http.RequestServices.GetRequiredService<PublicBaseUrl>().Value
        ?? throw new PublicBaseUrlNotSetException();

    private static string Issuer(HttpContext http) => $"{BaseUrl(http)}/op";

    /// <summary>
    /// The charset is part of what the recordings carry, and the uppercase spelling is the
    /// broker's. Passing the literal keeps it; the encoding overload would lowercase it.
    /// </summary>
    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{font:14px system-ui;margin:2rem;max-width:40rem}</style>
        </head><body><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</body></html>
        """;

    /// <summary>
    /// Every recorded JSON answer carries the same directive, success and failure alike. A
    /// client that cached a token response would reuse a code, so this is behaviour rather
    /// than decoration.
    /// </summary>
    private static IResult Json(string body) => new CachelessJson(body);

    private sealed class CachelessJson(string body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            http.Response.Headers.Pragma = "no-cache";

            return Results.Text(body, "application/json; charset=UTF-8").ExecuteAsync(http);
        }
    }

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
