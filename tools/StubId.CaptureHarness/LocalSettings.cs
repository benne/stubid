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
            return Resolve(value.GetString(), ExampleFor(name));
        }

        return null;
    }

    /// <summary>
    /// What a value read from the file actually resolves to, given the example file's value for
    /// the same name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate so it can be tested without a configuration file on disk, the same reason
    /// <see cref="ParseRedactions" /> is separate. The environment cannot stand in for one here:
    /// setting a variable to the empty string deletes it, so a test written that way exercises
    /// the absent path and proves nothing about the empty one.
    /// </para>
    /// <para>
    /// Two ways of not being filled in, and both used to read as a value. A blank line returned
    /// the empty string, which is a value of length zero and was treated as configured
    /// everywhere downstream — the preflight printed "set, 0 characters" and then complained it
    /// was under six, and an empty key identifier silenced the warning that a request object
    /// would name no key. The description text is the other: someone copied the example file and
    /// did not fill this one in, and sending that as a credential records a puzzling rejection
    /// instead of the exchange the case is for.
    /// </para>
    /// </remarks>
    public static string? Resolve(string? configured, string? example) =>
        string.IsNullOrEmpty(configured) || configured == example ? null : configured;

    /// <summary>The example file's value for a name, or null where it names it not at all.</summary>
    private static string? ExampleFor(string name) =>
        Example.Value?.RootElement.TryGetProperty(name, out var example) == true
        && example.ValueKind == JsonValueKind.String
            ? example.GetString()
            : null;

    /// <summary>
    /// Extra values to replace with placeholders when writing a fixture, as
    /// placeholder to value.
    /// </summary>
    /// <remarks>
    /// Needed because a recording made with a private client carries more than credentials.
    /// A transaction token's <c>recipient_info</c> names the receiving organization, so a
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

    /// <summary>
    /// The repository, found the same way the settings file is: by walking up for the solution.
    /// Null when the tool runs from somewhere that is not a checkout.
    /// </summary>
    public static string? Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                   && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "StubID.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName;
        }
    }

    private static JsonDocument? Load(string fileName)
    {
        if (Root is not { } root)
        {
            return null;
        }

        var path = System.IO.Path.Combine(root, fileName);
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
