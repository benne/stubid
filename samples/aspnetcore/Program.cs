using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using StubId.Client;

var builder = WebApplication.CreateBuilder(args);

// Two addresses, because StubID answers on two listeners and they are not interchangeable. The
// authority is the secured one: it is what the issuer names and where the browser is sent. The
// control port is plain HTTP on purpose, so that reading the certificate and creating a citizen
// never wait on a trust decision.
var authority = Setting("StubId:Authority");
var control = Setting("StubId:ControlUrl");
var clientId = Setting("StubId:ClientId");
var clientSecret = Setting("StubId:ClientSecret");

using var stubid = new StubIdClient(new Uri(control));

// The bootstrap that needs no trust: nothing has to be believed in order to fetch this. What it
// is used for below is the opposite of a relaxation - the handler ends up trusting this one
// certificate and nothing else, not even the roots this machine already has.
var certificate = await stubid.Runtime.GetTlsCertificateAsync()
    ?? throw new InvalidOperationException(
        $"{control} is serving plain HTTP, so there is no certificate to trust. Start StubID "
        + "with StubId__Tls=self-signed if you meant to secure it.");

var expected = certificate.RawData;


builder.Services.AddAuthorization();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        // The ordinary part, and it is ordinary: an application pointed at the real broker sets
        // exactly these, with a different authority and its own credentials.
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.ResponseType = "code";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("mitid");
        options.SaveTokens = true;

        // Without this the signed-in page has a subject and no person. The id_token this broker
        // issues says `idtoken_type: strict` and carries no mitid claim at all; the name and the
        // CPR flag are on the userinfo endpoint, which is what the recordings in
        // docs/brokers/neb/claims.md establish. Against the real broker you would need this line
        // for the same reason.
        options.GetClaimsFromUserInfoEndpoint = true;

        // And this, or fetching userinfo changes nothing you can see. What comes back from that
        // endpoint reaches the user only through a claim action, and the handler ships mappings
        // for the standard OpenID claims - not for a single one this broker names. Mapping all of
        // them also stops the handler discarding the protocol claims it usually does, so iss, exp
        // and at_hash end up on the page and in the cookie. That suits a page whose whole job is
        // to show what arrived; an application that needs three claims would map those three and
        // carry a smaller cookie for it.
        options.ClaimActions.MapAll();

        // The one line here you would not write against the real broker, whose certificate a
        // public authority already vouches for. It trusts the certificate this instance
        // generated and nothing else - not any certificate, which is the shortcut that outlives
        // the test it was written for. RequireHttpsMetadata is not mentioned, so it keeps its
        // default of true, and the authority above is https for that reason.
        options.BackchannelHttpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null
                && CryptographicOperations.FixedTimeEquals(presented.RawData, expected),
        };

        // A real application handles a refused login rather than letting the exception escape.
        // Aborting on StubID's page arrives here, and so does a queued refusal.
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.HandleResponse();
            return Task.CompletedTask;
        };
    });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Html(
    "<h1>A sample relying party</h1>"
    + "<p>Nothing here is signed in yet.</p>"
    + "<p><a href=\"/secure\">Sign in with MitID</a></p>"));

app.MapGet("/secure", (HttpContext context) => Html(
    "<h1>Signed in</h1>"
    + "<p>These are the claims StubID issued. Against the real broker they would be the same "
    + "names, with the same JSON types.</p>"
    + "<table>"
    + string.Concat(context.User.Claims.Select(claim =>
        $"<tr><td><code>{Escape(claim.Type)}</code></td><td>{Escape(claim.Value)}</td></tr>"))
    + "</table>"
    + "<p><a href=\"/signout\">Sign out</a></p>"))
    .RequireAuthorization();

// Signing out of the application and out of StubID, which is what an id_token_hint is for. The
// cookie scheme comes first because the redirect back has to find the session already gone.
app.MapGet("/signout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    [
        CookieAuthenticationDefaults.AuthenticationScheme,
        OpenIdConnectDefaults.AuthenticationScheme,
    ]));

app.Run();

string Setting(string key) =>
    builder.Configuration[key]
    ?? throw new InvalidOperationException($"{key} is not configured.");

static IResult Html(string body) =>
    Results.Content(
        "<!doctype html><meta charset=\"utf-8\"><title>StubID sample</title>"
        + "<style>body{font-family:system-ui;margin:3rem;max-width:48rem}"
        + "td{padding:.15rem .75rem .15rem 0;vertical-align:top}</style>"
        + body,
        "text/html");

// Claim values come from a token. Encoding them is not paranoia about StubID; it is what the
// same page would have to do against the real broker.
static string Escape(string value) => WebUtility.HtmlEncode(value);

/// <summary>Named so the test that proves this sample works can host it.</summary>
public partial class Program;
