using StubId.Profiles;

namespace StubId.Server;

/// <summary>Which broker an instance emulates, and how it is chosen.</summary>
/// <remarks>
/// One instance serves one broker. Serving two at once is expressible — the route loader takes a
/// list of tenants and each declares its own root — but it is not what a suite wants: a test points
/// a client library at one authority, and an instance that answered as two would have to scope the
/// control API, the admin pages and the fidelity ledger to a broker at every turn to say which one
/// it was talking about.
/// </remarks>
public static class BrokerProfiles
{
    /// <summary>What an instance emulates when nothing says otherwise.</summary>
    /// <remarks>
    /// The first broker, so that every suite written before this setting existed keeps working
    /// without being told about it.
    /// </remarks>
    public const string Default = "neb";

    /// <summary>The setting that chooses one.</summary>
    public const string Setting = "StubId:Profile";

    /// <summary>The brokers this build can serve, by the name the setting takes.</summary>
    public static IReadOnlyList<string> Available => ["neb"];

    /// <summary>
    /// Reads the setting, and refuses an unknown name rather than falling back to the default.
    /// </summary>
    /// <remarks>
    /// A typo that silently started the wrong broker would be found by a client library failing to
    /// discover a document, several layers away from the environment variable that caused it.
    /// </remarks>
    public static IBrokerProfile Select(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Select(configuration[Setting]);
    }

    /// <summary>The same choice, made from the name alone.</summary>
    /// <remarks>
    /// For the hosting packages, which know which profile they configured and have to say what
    /// authority a client library should be pointed at before the instance has started. They read
    /// it here rather than keeping a table of their own, so there is one place a broker's root is
    /// written down.
    /// </remarks>
    public static IBrokerProfile Select(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Default;
        }

        return name.Trim() switch
        {
            "neb" => new NetsEidBrokerProfile(),
            _ => throw new InvalidOperationException(
                $"{Setting} is '{name}', which is not a broker this build serves. "
                + $"It takes one of: {string.Join(", ", Available)}."),
        };
    }
}
