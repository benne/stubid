using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace StubId.Profiles.Idura;

/// <summary>
/// The path segment Idura keys its per-method metadata by: standard base64 of an acr value.
/// </summary>
/// <remarks>
/// <para>
/// Standard base64 with padding, not base64url. That is not a detail: <c>-</c> and <c>_</c>
/// are not standard-base64 characters, which is what keeps a root-mounted tenant's dynamic
/// first segment from ever swallowing StubID's own <c>/_stubid/…</c> surface. Assuming
/// base64url would give that away.
/// </para>
/// <para>
/// A policy instance rather than a named constraint, because a name resolves through the
/// application-wide constraint map, where a second profile registering the same name with
/// different meaning would collide across the whole application.
/// </para>
/// </remarks>
public sealed class AcrSegmentPolicy(IReadOnlyCollection<string> vocabulary) : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (values.TryGetValue(routeKey, out var raw) && raw is string segment)
        {
            return TryDecode(segment, out var acr) && vocabulary.Contains(acr);
        }

        return false;
    }

    /// <summary>Decodes a segment, or reports that it is not one. Case-sensitive by nature.</summary>
    public static bool TryDecode(string segment, out string value)
    {
        value = "";

        // Rejecting the base64url alphabet here is what the comment above is about.
        if (segment.Length == 0 || segment.Contains('-') || segment.Contains('_'))
        {
            return false;
        }

        Span<byte> buffer = new byte[segment.Length];
        if (!Convert.TryFromBase64String(segment, buffer, out var written))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(buffer[..written]);
        return true;
    }
}
