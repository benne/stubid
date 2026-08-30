using Microsoft.AspNetCore.Http;

namespace StubId.Server;

/// <summary>
/// How the broker matches a path, which is not how a framework matches one.
/// </summary>
/// <remarks>
/// <para>
/// Probed against pre-production rather than assumed, and the answer is a combination no
/// framework produces on its own:
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
/// </remarks>
public sealed class PathRules(string pathBase)
{
    public bool Accepts(PathString path)
    {
        var value = path.Value;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // The proxy's prefix match: exact case, and a segment boundary after it.
        if (!value.StartsWith(pathBase, StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Length > pathBase.Length && value[pathBase.Length] != '/')
        {
            return false;
        }

        // Refused below the base, though routing would otherwise accept it.
        return !(value.Length > pathBase.Length + 1 && value.EndsWith('/'));
    }
}
