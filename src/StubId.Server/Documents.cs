using System.Reflection;
using System.Text;

namespace StubId.Server;

/// <summary>
/// Serves the recorded broker documents with the host swapped for ours.
/// </summary>
/// <remarks>
/// Substitution on the raw text, never a parse and re-serialise. The discovery document's
/// member order, its lack of whitespace, and the three members the broker leaves out are all
/// part of what a client sees, and all three are destroyed by a round trip through a JSON
/// object.
/// </remarks>
public sealed class Documents
{
    private const string RecordedHost = "https://pp.netseidbroker.dk";

    private readonly string _discoveryTemplate;

    public Documents()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("discovery.json")
            ?? throw new InvalidOperationException("The recorded discovery document is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _discoveryTemplate = reader.ReadToEnd();
    }

    /// <summary>
    /// The discovery document for a given public base URL, e.g. <c>http://localhost:5000</c>.
    /// The issuer keeps the recorded path segment, so it ends in <c>/op</c>.
    /// </summary>
    public string Discovery(string baseUrl) =>
        _discoveryTemplate.Replace(RecordedHost, baseUrl.TrimEnd('/'), StringComparison.Ordinal);
}
