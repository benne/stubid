using System.Text;
using StubId.Abstractions;
using StubId.Profiles;

namespace StubId.Server.Signicat;

/// <summary>
/// The Signicat route table: what the unattended recordings settle, and a reason for the rest.
/// </summary>
/// <remarks>
/// The discovery document is served from the recording rather than composed, so it advertises
/// every endpoint the broker has. Trimming it to what is reproduced would be less faithful, not
/// more - a client library that keys off metadata being absent would behave differently here than
/// against the broker. So each advertised endpoint is declared, and the ones no recording settles
/// say plainly what they are.
/// <para>
/// Every 501 is a named method with its own annotation, rather than one shared handler. The route
/// table and the ledger are then the same list twice over: an endpoint that stops being unemulated
/// has to stop claiming to be, and <c>/_stubid/v1/routes</c> reads the provenance from the method
/// it is about to call.
/// </para>
/// </remarks>
internal static class SignicatEndpoints
{
    private const string NoLoginYet = "docs/brokers/signicat/divergences.md#no-login-yet";

    private const string OutsideALogin = "docs/brokers/signicat/divergences.md#outside-a-mitid-login";

    private const string CibaIsNotEmulated = "docs/brokers/signicat/divergences.md#ciba";

    /// <summary>
    /// Patterns carry the tenant root, the way the first broker's do, so the host composes any
    /// mount prefix and the declarations stay the same.
    /// </summary>
    public static IReadOnlyList<RouteDeclaration> Declare()
    {
        var routes = new List<RouteDeclaration>();

        void Map(string pattern, string[] methods, RouteRole role, Delegate handler) =>
            routes.Add(new RouteDeclaration(pattern, methods, role, handler)
            {
                // The two root segments are compared exactly, where the first broker compares only
                // its own: a request that capitalizes either one is refused by this broker. What
                // happens below them was never probed, so the rest keeps the default rather than
                // claiming a strictness nothing measured.
                Exactness = new SegmentExactness(
                    [StringComparison.Ordinal, StringComparison.Ordinal], TrailingSlash.Refuse),
            });

        Map("auth/open/.well-known/openid-configuration", ["GET"], RouteRole.Discovery, Discovery);
        Map("auth/open/.well-known/openid-configuration/jwks", ["GET"], RouteRole.Jwks, KeySet);

        // Both verbs on everything that is not reproduced, which is the first broker's rule for
        // its one such endpoint: answering 405 would say the method was wrong, when what is wrong
        // is that there is nothing behind the route at all.
        Map("auth/open/connect/authorize", ["GET", "POST"], RouteRole.Authorize, Authorize);
        Map("auth/open/connect/token", ["GET", "POST"], RouteRole.Token, Token);
        Map("auth/open/connect/userinfo", ["GET", "POST"], RouteRole.UserInfo, UserInfo);
        Map("auth/open/connect/endsession", ["GET", "POST"], RouteRole.Extra("end-session"), EndSession);
        Map("auth/open/connect/checksession", ["GET", "POST"], RouteRole.Extra("check-session"), CheckSession);
        Map("auth/open/connect/par", ["GET", "POST"], RouteRole.Par, Par);
        Map("auth/open/connect/revocation", ["GET", "POST"], RouteRole.Extra("revocation"), Revocation);
        Map("auth/open/connect/introspect", ["GET", "POST"], RouteRole.Extra("introspection"), Introspection);
        Map("auth/open/connect/deviceauthorization", ["GET", "POST"],
            RouteRole.Extra("device-authorization"), DeviceAuthorization);
        Map("auth/open/connect/ciba", ["GET", "POST"], RouteRole.Extra("ciba"), Ciba);
        Map("auth/open/connect/ciba/cancel", ["GET", "POST"], RouteRole.Extra("ciba-cancel"), CibaCancel);

        return routes;
    }

    /// <summary>The recorded discovery document, with this instance's address in place of the tenant's.</summary>
    /// <remarks>
    /// Its <c>claims_supported</c> is the recorded account's configuration rather than a fact about
    /// the platform, and the document says so by being the recording: a composed one would have to
    /// invent a list.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/signicat/sandbox/CAP-001")]
    private static IResult Discovery(HttpContext http, Documents documents) =>
        Served(documents.Discovery("signicat", BaseUrl(http)));

    /// <summary>The key set, in the member shape this broker publishes.</summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/signicat/sandbox/CAP-002")]
    private static IResult KeySet(HttpContext http, Keys keys) => Served(SignicatKeySet.Write(keys));

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-009, "
                   + "fixtures/signicat/sandbox/CAP-013",
        Reason = NoLoginYet)]
    private static IResult Authorize(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-016, "
                   + "fixtures/signicat/sandbox/CAP-017, fixtures/signicat/sandbox/CAP-018",
        Reason = NoLoginYet)]
    private static IResult Token(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-019",
        Reason = NoLoginYet)]
    private static IResult UserInfo(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-043",
        Reason = NoLoginYet)]
    private static IResult EndSession(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = NoLoginYet)]
    private static IResult CheckSession(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-040, "
                   + "fixtures/signicat/sandbox/CAP-041, fixtures/signicat/sandbox/CAP-042",
        Reason = NoLoginYet)]
    private static IResult Par(HttpContext http) => NotEmulated.Answer(http, NoLoginYet);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = OutsideALogin)]
    private static IResult Revocation(HttpContext http) => NotEmulated.Answer(http, OutsideALogin);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = OutsideALogin)]
    private static IResult Introspection(HttpContext http) => NotEmulated.Answer(http, OutsideALogin);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = OutsideALogin)]
    private static IResult DeviceAuthorization(HttpContext http) => NotEmulated.Answer(http, OutsideALogin);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = CibaIsNotEmulated)]
    private static IResult Ciba(HttpContext http) => NotEmulated.Answer(http, CibaIsNotEmulated);

    [Fidelity(FidelityTier.OutOfContract, FidelityProvenance.NotEmulated,
        Evidence = "fixtures/signicat/sandbox/CAP-001",
        Reason = CibaIsNotEmulated)]
    private static IResult CibaCancel(HttpContext http) => NotEmulated.Answer(http, CibaIsNotEmulated);

    /// <summary>
    /// A document, served the way this broker serves one.
    /// </summary>
    /// <remarks>
    /// No cache headers, because neither recording carries any. The first broker sends
    /// <c>Cache-Control: public, max-age=770</c> on both and StubID answers <c>no-store</c>, which
    /// is a divergence of its own; here the recording and the answer agree by saying nothing.
    /// <para>
    /// The content type is written as the header rather than handed to <c>Results.Text</c>, which
    /// parses it and lowercases the charset. The broker spells it <c>UTF-8</c>, and a document
    /// compared byte for byte is worth a header spelled the same way. Writing it means writing the
    /// length as well: without one Kestrel frames the answer as chunked, which is a header no
    /// recording carries in place of one every recorded answer has.
    /// </para>
    /// </remarks>
    private static IResult Served(string body) => new SignicatDocument(body);

    private sealed class SignicatDocument(string body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            ArgumentNullException.ThrowIfNull(http);

            http.Response.ContentType = "application/json; charset=UTF-8";
            http.Response.ContentLength = Encoding.UTF8.GetByteCount(body);

            return http.Response.WriteAsync(body);
        }
    }

    /// <summary>The address this instance was told to answer at, never the one the request arrived on.</summary>
    private static string BaseUrl(HttpContext http) =>
        http.RequestServices.GetRequiredService<PublicBaseUrl>().Value
        ?? throw new PublicBaseUrlNotSetException();
}
