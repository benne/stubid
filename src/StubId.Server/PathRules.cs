using Microsoft.AspNetCore.Http;
using StubId.Profiles;

namespace StubId.Server;

/// <summary>
/// How the broker matches a path, which is not how a framework matches one.
/// </summary>
/// <remarks>
/// <para>
/// Probed against pre-production rather than assumed, and the answer for Nets eID Broker is a
/// combination no framework produces on its own:
/// </para>
/// <code>
/// /op/.well-known/openid-configuration        200
/// /op/.well-known/OPENID-CONFIGURATION        200   case-insensitive below the base
/// /op/.WELL-KNOWN/openid-configuration        200
/// /OP/.well-known/openid-configuration        404   but the base itself is case-sensitive
/// /op/.well-known/openid-configuration/       404   and a trailing slash is refused
/// </code>
/// <para>
/// The split is the deployment showing through: a reverse proxy selects the application by a
/// case-sensitive path prefix, and the application beneath it matches case-insensitively.
/// StubID reproduces both halves, because being stricter than the broker fails a client that
/// works against it, and being looser passes one that does not.
/// </para>
/// <para>
/// None of that is one rule for the whole application. It is what one broker's
/// <see cref="TenantRoot" /> declares, and the profile that declares it is the only thing that
/// knows — a broker serving at the host root has no prefix to compare at all, and gets the whole
/// of the first clause skipped rather than a base of <c>""</c> that happens to match everything.
/// </para>
/// </remarks>
public sealed class PathRules(TenantRoot root)
{
    public bool Accepts(PathString path)
    {
        var value = path.Value;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var prefix = root.Prefix;

        if (prefix.Length > 0)
        {
            // The proxy's prefix match: the comparison the profile declares, and a segment
            // boundary after it.
            if (!value.StartsWith(prefix, root.Comparison))
            {
                return false;
            }

            if (value.Length > prefix.Length && value[prefix.Length] != '/')
            {
                return false;
            }
        }

        // Refused below the base, though routing would otherwise accept it. The root itself with
        // one trailing slash is not below the base and is left to the router, which is why this
        // counts from a character past the prefix rather than from its end.
        return root.TrailingSlash is not TrailingSlash.Refuse
            || !(value.Length > prefix.Length + 1 && value.EndsWith('/'));
    }
}
