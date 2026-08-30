using System.Text.Json;

namespace StubId.CaptureHarness;

/// <summary>
/// Credentials and redaction rules for recording, kept outside the repository.
/// </summary>
/// <remarks>
/// Reads the environment first, then <c>capture.local.json</c> at the repository root. The
/// file uses the same names as the environment variables so there is only one vocabulary to
/// learn, and it is gitignored.
/// </remarks>
public static class LocalSettings
{
    private const string FileName = "capture.local.json";

    private static readonly Lazy<JsonDocument?> File = new(() => Load(FileName));
    private static readonly Lazy<JsonDocument?> Example = new(() => Load("capture.local.example.json"));

    /// <summary>
    /// Environment first so a one-off run can override the file without editing it.
    /// </summary>
    public static string? Get(string name)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (File.Value?.RootElement.TryGetProperty(name, out var value) == true
            && value.ValueKind == JsonValueKind.String)
        {
            var configured = value.GetString();

            // Someone copied the example file and did not fill this one in. Treating it as
            // set would send the description text as a credential and record a puzzling
            // rejection instead of the exchange the case is for.
            return IsExampleText(name, configured) ? null : configured;
        }

        return null;
    }

    private static bool IsExampleText(string name, string? configured)
    {
        if (string.IsNullOrEmpty(configured) || Example.Value is null)
        {
            return false;
        }

        return Example.Value.RootElement.TryGetProperty(name, out var example)
            && example.ValueKind == JsonValueKind.String
            && example.GetString() == configured;
    }

    /// <summary>
    /// Extra values to replace with placeholders when writing a fixture, as
    /// placeholder to value.
    /// </summary>
    /// <remarks>
    /// Needed because a recording made with a private client carries more than credentials.
    /// A transaction token's <c>recipient_info</c> names the receiving organisation, so a
    /// fixture would otherwise publish a company's name and CVR number.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Redactions()
    {
        if (File.Value?.RootElement.TryGetProperty("redact", out var redact) != true
            || redact.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        return ParseRedactions(redact);
    }

    /// <summary>
    /// Reads the redaction rules out of a "redact" object. Separate so it can be tested
    /// without a configuration file on disk.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRedactions(JsonElement redact) =>
        redact.EnumerateObject()
            // "//" is how the example file carries its comments. Treating one as a rule would
            // replace the comment's own text wherever it appeared in a recording.
            .Where(m => m.Value.ValueKind == JsonValueKind.String
                        && !m.Name.StartsWith("//", StringComparison.Ordinal))
            .ToDictionary(m => m.Name, m => m.Value.GetString()!, StringComparer.Ordinal);

    public static string? Path { get; private set; }

    private static JsonDocument? Load(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var path = System.IO.Path.Combine(directory.FullName, fileName);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        if (fileName == FileName)
        {
            Path = path;
        }

        return JsonDocument.Parse(System.IO.File.ReadAllText(path));
    }
}
