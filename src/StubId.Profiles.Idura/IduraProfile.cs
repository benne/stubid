using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace StubId.Profiles.Idura;

/// <summary>A client as Idura's own SDK expects to find it.</summary>
public sealed record IduraClient(string ClientId, string UserInfoResponseStrategy = "plainJson");

/// <summary>
/// Idura's route table.
/// </summary>
/// <remarks>
/// <para>
/// A spike, and honest about it: the routes are declared and the two that can be answered
/// without an authentication are answered. Everything else reports that it is not implemented
/// rather than inventing bytes nobody has recorded. Its purpose is to find out whether the
/// seam can express a second broker at all, before the seam is built on.
/// </para>
/// <para>
/// What it exercises that Nets eID Broker never would: an issuer at the bare host with no path
/// segment, a dynamic path segment appearing *before* a literal <c>.well-known</c>, that
/// segment applying to only two of the routes, case-insensitive matching that tolerates a
/// trailing slash, and an endpoint whose status depends on the query string.
/// </para>
/// </remarks>
public sealed class IduraProfile(IReadOnlyList<IduraClient> clients) : IBrokerProfile
{
    /// <summary>The acr values a tenant will answer for, which the path segment is checked against.</summary>
    private static readonly string[] Vocabulary =
    [
        "urn:grn:authn:dk:mitid:low",
        "urn:grn:authn:dk:mitid:substantial",
        "urn:grn:authn:dk:mitid:high",
        "urn:grn:authn:dk:mitid:business",
    ];

    public ProfileId Id => new("idura", "2026.08-spike");

    public IReadOnlyList<RouteDeclaration> DeclareRoutes(ProfileContext context)
    {
        // Tolerant where Idura is tolerant. Being stricter than the broker fails a client that
        // works against it, which is the same error as being looser, pointing the other way.
        var exactness = SegmentExactness.Uniform(
            StringComparison.OrdinalIgnoreCase, TrailingSlash.Tolerate);

        var acr = new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal)
        {
            ["acr"] = new AcrSegmentPolicy(Vocabulary),
        };

        RouteDeclaration Route(
            string pattern, string[] methods, RouteRole role, Delegate handler,
            IReadOnlyDictionary<string, IParameterPolicy>? policies = null) =>
            new(pattern, methods, role, handler)
            {
                Exactness = exactness,
                ParameterPolicies = policies ?? new Dictionary<string, IParameterPolicy>(StringComparer.Ordinal),
            };

        return
        [
            Route(".well-known/openid-configuration", ["GET"], RouteRole.Discovery,
                () => NotImplemented("discovery")),

            // The dynamic segment applies here and to authorize, and to nothing else. Idura
            // 404s it in front of the key set and the token endpoint, so a stub that served
            // them there would pass a client the real broker would refuse.
            Route("{acr}/.well-known/openid-configuration", ["GET"], RouteRole.Extra("acr-discovery"),
                (string acr) => NotImplemented($"discovery scoped to {Decoded(acr)}"), acr),

            Route(".well-known/jwks", ["GET"], RouteRole.Jwks, () => NotImplemented("key set")),

            Route("oauth2/authorize", ["GET", "POST"], RouteRole.Authorize,
                () => NotImplemented("authorize")),
            Route("{acr}/oauth2/authorize", ["GET", "POST"], RouteRole.Extra("acr-authorize"),
                (string acr) => NotImplemented($"authorize pinned to {Decoded(acr)}"), acr),

            Route("oauth2/token", ["POST"], RouteRole.Token, () => NotImplemented("token")),
            Route("oauth2/userinfo", ["GET", "POST"], RouteRole.UserInfo, () => NotImplemented("userinfo")),
            Route("oauth2/logout", ["GET"], RouteRole.Extra("logout"), () => NotImplemented("logout")),
            Route("oauth2/par", ["POST"], RouteRole.Par, () => NotImplemented("pushed authorization")),

            // Undocumented, and the SDK refuses to initialise without it. Its status depends on
            // the query string, which routing cannot express - only a handler can.
            Route(".well-known/criipto-configuration", ["GET"], RouteRole.Extra("criipto-configuration"),
                (HttpContext http) => Configuration(http)),
        ];
    }

    /// <summary>
    /// Answers the SDK's configuration probe. It looks for its own client id inside the
    /// clients array and throws before authorize is ever reached if it is not there, so a
    /// fixed body would break every real client.
    /// </summary>
    private IResult Configuration(HttpContext http)
    {
        var requested = http.Request.Query["client_id"].ToString();

        var matching = string.IsNullOrEmpty(requested)
            ? clients
            : clients.Where(c => c.ClientId == requested).ToList();

        if (!string.IsNullOrEmpty(requested) && matching.Count == 0)
        {
            return Results.NotFound();
        }

        return Results.Json(new
        {
            clients = matching.Select(c => new
            {
                client_id = c.ClientId,
                userinfo_response_strategy = c.UserInfoResponseStrategy,
            }),
        });
    }

    private static string Decoded(string segment) =>
        AcrSegmentPolicy.TryDecode(segment, out var value) ? value : segment;

    /// <summary>
    /// Says so rather than inventing bytes. No Idura login has been recorded, and guessing at
    /// a shape is what this project exists to avoid.
    /// </summary>
    private static IResult NotImplemented(string what) => Results.Json(
        new { error = "not_implemented", detail = $"StubID does not emulate Idura's {what} yet." },
        statusCode: StatusCodes.Status501NotImplemented);
}
