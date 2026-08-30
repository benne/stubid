using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace StubId.Server;

/// <summary>
/// Refuses to start rather than failing on every request.
/// </summary>
/// <remarks>
/// Duplicate routes do not fail fast on their own. The matcher is built lazily on the first
/// request, so two profiles declaring the same path start silently and then throw on every
/// request afterwards — and the compiler's own duplicate-route analyser only sees literal
/// Map calls in source, so it is blind to routes a profile declares. Enumerating the set at
/// startup and checking it is the only thing that catches it before a user does.
/// </remarks>
public static class RouteCollisionScan
{
    public static void Verify(IReadOnlyList<Endpoint> endpoints)
    {
        var collisions = endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(Keys)
            .GroupBy(k => k.Key, StringComparer.Ordinal)

            // Counting the entries, not the distinct names: two profiles declaring an
            // identical route produce identical names, and deduplicating by name made the
            // collision invisible to the check written to catch it.
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is declared {g.Count()} times, by {string.Join(" and ", g.Select(k => k.Endpoint))}")
            .ToList();

        if (collisions.Count > 0)
        {
            throw new InvalidOperationException(
                "Two routes would match the same request, which throws on every request rather "
                + "than at startup if left alone: " + string.Join("; ", collisions));
        }
    }

    /// <summary>
    /// One key per method, so two routes at the same path answering different methods are not
    /// a collision. The key includes each parameter's policy, because a base64 segment and a
    /// GUID segment at the same position coexist legitimately - and rejecting that would
    /// reject exactly the route the seam was built to support.
    ///
    /// It is a conservative check, not a proof: two different policies that some single value
    /// satisfies remain ambiguous at request time, and nothing short of solving both
    /// constraints can say otherwise.
    /// </summary>
    private static IEnumerable<(string Key, string Endpoint)> Keys(RouteEndpoint endpoint)
    {
        var shape = string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment =>
            string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content.ToLowerInvariant(),
                RoutePatternParameterPart parameter => "{" + string.Join(',',
                    parameter.ParameterPolicies.Select(policy =>
                        // A policy supplied as an instance carries no textual content, so its
                        // type is what distinguishes it. Without this every constrained
                        // parameter keys as the same empty shape and two legitimately
                        // different ones look identical.
                        policy.Content ?? policy.ParameterPolicy?.GetType().FullName ?? "?")) + "}",
                _ => "{?}",
            }))));

        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];

        foreach (var method in methods)
        {
            yield return ($"{method} {shape}", endpoint.DisplayName ?? shape);
        }
    }
}
