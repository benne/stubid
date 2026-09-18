using System.Text;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What the build embeds is the recording with the broker's host taken out of it.
/// </summary>
/// <remarks>
/// Two things have to hold at once. The shipped assembly must not carry the broker's hostname,
/// and the template must still be the recording byte for byte everywhere else, because that is
/// what makes the served document the recording rather than something rebuilt to look like it.
/// </remarks>
public class EmbeddedDocumentTests
{
    private const string PlaceholderHost = "https://stubid.invalid";

    /// <summary>
    /// Each derived template: the recording it comes from, and the host the build took out of it.
    /// </summary>
    /// <remarks>
    /// The second broker's recording carries a placeholder rather than a hostname, because its
    /// tenant is in the host and nothing committed may name the account a recording came from. The
    /// substitution is the same either way: one string out, one string in.
    /// </remarks>
    private static readonly (string Broker, string Resource, string Recording, string Host)[] Derived =
    [
        ("neb", "discovery.json", "fixtures/neb/pp/CAP-001/response.raw", "https://pp.netseidbroker.dk"),
        ("signicat", "discovery.signicat.json", "fixtures/signicat/sandbox/CAP-001/response.raw",
            "https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com"),
    ];

    public static TheoryData<string, string, string> Templates()
    {
        var rows = new TheoryData<string, string, string>();

        foreach (var (_, resource, recording, host) in Derived)
        {
            rows.Add(resource, recording, host);
        }

        return rows;
    }

    /// <summary>
    /// Every broker this build serves has a template here.
    /// </summary>
    /// <remarks>
    /// The rows are written out, because which recording a template comes from is not derivable
    /// from a broker's name. This is what stops a third broker arriving with a template nothing
    /// above ever reads.
    /// </remarks>
    [Fact]
    public void Every_broker_this_build_serves_has_a_derived_template()
    {
        Assert.Equal(
            BrokerProfiles.Available.Order(StringComparer.Ordinal),
            Derived.Select(row => row.Broker).Order(StringComparer.Ordinal));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private static Stream TemplateStream(string resource) =>
        typeof(Documents).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The derived template {resource} is missing.");

    /// <summary>Read the way <see cref="Documents"/> reads it, from the same assembly.</summary>
    private static string EmbeddedTemplate(string resource)
    {
        using var stream = TemplateStream(resource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Task<string> RecordingAsync(string recording) => File.ReadAllTextAsync(
        Path.Combine(RepositoryRoot(), recording.Replace('/', Path.DirectorySeparatorChar)), Ct);

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_broker_host_is_nowhere_in_the_shipped_document(
        string resource, string recording, string brokerHost)
    {
        _ = recording;

        Assert.DoesNotContain(
            brokerHost.Replace("https://", "", StringComparison.Ordinal),
            EmbeddedTemplate(resource),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task The_template_is_the_recording_with_the_host_swapped(
        string resource, string recording, string brokerHost)
    {
        // The derivation is one substitution and nothing else. Anything the build did beyond
        // that - a rewritten line ending, a re-serialized member order - shows up here. A byte
        // order mark does not. Both sides are read through a reader that drops one, so the
        // bytes have a test of their own.
        var recorded = await RecordingAsync(recording);

        Assert.Equal(
            recorded,
            EmbeddedTemplate(resource).Replace(PlaceholderHost, brokerHost, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_shipped_document_carries_no_byte_order_mark(
        string resource, string recording, string brokerHost)
    {
        _ = recording;
        _ = brokerHost;

        // A mark costs a client nothing, because Documents reads the resource through the same
        // reader and never sees one. It is still a byte the build added to the recording, and
        // the embedded template is meant to be the recording with the host swapped and nothing
        // else, so the raw bytes are where it has to be caught.
        using var stream = TemplateStream(resource);

        var head = new byte[3];
        stream.ReadExactly(head);

        Assert.False(head is [0xEF, 0xBB, 0xBF], "The build wrote a UTF-8 byte order mark.");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task The_placeholder_is_not_something_the_recording_already_said(
        string resource, string recording, string brokerHost)
    {
        _ = resource;
        _ = brokerHost;

        // If the broker ever served this string itself, swapping it back would be lossy and
        // the test above would be checking nothing.
        Assert.DoesNotContain(PlaceholderHost, await RecordingAsync(recording), StringComparison.Ordinal);
    }
}
