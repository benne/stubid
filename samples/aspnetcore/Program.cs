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

        // A refused login is an ordinary outcome rather than a crash, and a real application
        // answers it with a page instead of letting the exception escape. The framework already
        // makes the distinction worth making: a refusal reaches the client as access_denied and
        // is routed here rather than to a failure. Somebody aborting on StubID's page arrives
        // this way, and so does a queued refusal and a login nobody decided in time.
        options.Events.OnAccessDenied = async context =>
        {
            context.HandleResponse();
            await Refused(context.HttpContext);
        };

        // Everything else, which is a fault rather than a decision: a correlation cookie that
        // did not survive the round trip, a token that failed validation, a refusal the broker
        // chose to send under some other OAuth error. Those keep a status code that says so.
        options.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            await Answer(context.HttpContext, StatusCodes.Status400BadRequest, Page(
                "<h1>The sign-in did not complete</h1>"
                + $"<p>{Escape(context.Failure?.Message ?? "Nothing was reported about why.")}</p>"
                + "<p><a href=\"/\">Start over</a></p>"));
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

static IResult Html(string body) => Results.Content(Page(body), "text/html");

// Shared with the two authentication events above, which write to the response themselves: an
// event has no IResult to return.
static string Page(string body) =>
    "<!doctype html><meta charset=\"utf-8\"><title>StubID sample</title>"
    + "<style>body{font-family:system-ui;margin:3rem;max-width:48rem}"
    + "td{padding:.15rem .75rem .15rem 0;vertical-align:top}</style>"
    + body;

static async Task Answer(HttpContext http, int status, string page)
{
    http.Response.StatusCode = status;
    http.Response.ContentType = "text/html";
    await http.Response.WriteAsync(page);
}

// What StubID said, rather than a description of it. The broker's own code travels in
// error_description - mitid_user_aborted for somebody who aborted, mitid_timeout for a login
// nobody decided - and that code is the part worth reading, so the page shows it instead of
// swallowing it. This client asked for form_post, so the refusal arrives as a form; the handler
// has already read it and reading it again returns the same cached collection.
static async Task Refused(HttpContext http)
{
    var reported = http.Request.HasFormContentType
        ? (await http.Request.ReadFormAsync())["error_description"].ToString()
        : http.Request.Query["error_description"].ToString();

    var code = reported.Length > 0 ? reported : "(none)";

    await Answer(http, StatusCodes.Status200OK, Page(
        "<h1>The login was refused</h1>"
        + "<p>Nobody is signed in. StubID sent back <code>error=access_denied</code> with "
        + $"<code>error_description={Escape(code)}</code>, which is the pair the real broker "
        + "sends for a login that did not go through.</p>"
        + "<p><a href=\"/secure\">Try again</a></p>"));
}

// Claim values come from a token. Encoding them is not paranoia about StubID; it is what the
// same page would have to do against the real broker.
static string Escape(string value) => WebUtility.HtmlEncode(value);

/// <summary>Named so the test that proves this sample works can host it.</summary>
public partial class Program;
