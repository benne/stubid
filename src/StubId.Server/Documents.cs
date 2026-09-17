using System.Reflection;
using System.Text;

namespace StubId.Server;

/// <summary>
/// Serves the recorded broker documents with the host swapped for ours.
/// </summary>
/// <remarks>
/// Substitution on the raw text, never a parse and re-serialize. The discovery document's
/// member order, its lack of whitespace, and the three members the broker leaves out are all
/// part of what a client sees, and all three are destroyed by a round trip through a JSON
/// object.
/// <para>
/// The embedded template is the recording with the broker's host swapped for a placeholder at
/// build time, so the recording stays the single source of truth without the broker's hostname
/// reaching the shipped assembly. Serving is the same substitution one step further on.
/// </para>
/// </remarks>
public sealed class Documents
{
    private const string PlaceholderHost = "https://stubid.invalid";

    /// <summary>
    /// One template per broker, each derived from that broker's own recording.
    /// </summary>
    /// <remarks>
    /// Read once at startup rather than per request, and both are read whichever broker this
    /// instance serves: a resource that had gone missing from the build would otherwise be found
    /// only by whoever ran that profile.
    /// </remarks>
    private readonly Dictionary<string, string> _discoveryTemplates = new(StringComparer.Ordinal)
    {
        ["neb"] = Template("discovery.json"),
        ["signicat"] = Template("discovery.signicat.json"),
    };

    /// <summary>
    /// The first broker's discovery document for a given public base URL, e.g.
    /// <c>http://localhost:5000</c>. The issuer keeps the recorded path segment, so it ends in
    /// <c>/op</c>.
    /// </summary>
    public string Discovery(string baseUrl) => Discovery(BrokerProfiles.Default, baseUrl);

    /// <summary>
    /// The named broker's discovery document, whose issuer ends with that broker's own root.
    /// </summary>
    /// <remarks>
    /// Internal because a caller outside this assembly reaches the document through the instance
    /// it is served by, which already knows which broker it is.
    /// </remarks>
    internal string Discovery(string broker, string baseUrl) =>
        _discoveryTemplates[broker].Replace(PlaceholderHost, baseUrl.TrimEnd('/'), StringComparison.Ordinal);

    private static string Template(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The derived template {resource} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
