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
    private const string BrokerHost = "https://pp.netseidbroker.dk";
    private const string PlaceholderHost = "https://stubid.invalid";

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

    private static Stream TemplateStream() =>
        typeof(Documents).Assembly.GetManifestResourceStream("discovery.json")
            ?? throw new InvalidOperationException("The derived discovery template is missing.");

    /// <summary>Read the way <see cref="Documents"/> reads it, from the same assembly.</summary>
    private static string EmbeddedTemplate()
    {
        using var stream = TemplateStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Task<string> RecordingAsync() => File.ReadAllTextAsync(
        Path.Combine(RepositoryRoot(), "fixtures", "neb", "pp", "CAP-001", "response.raw"), Ct);

    [Fact]
    public void The_broker_host_is_nowhere_in_the_shipped_document()
    {
        Assert.DoesNotContain("netseidbroker.dk", EmbeddedTemplate(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_template_is_the_recording_with_the_host_swapped()
    {
        // The derivation is one substitution and nothing else. Anything the build did beyond
        // that - a rewritten line ending, a re-serialised member order - shows up here. A byte
        // order mark does not. Both sides are read through a reader that drops one, so the
        // bytes have a test of their own.
        var recorded = await RecordingAsync();

        Assert.Equal(
            recorded,
            EmbeddedTemplate().Replace(PlaceholderHost, BrokerHost, StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipped_document_carries_no_byte_order_mark()
    {
        // A mark costs a client nothing, because Documents reads the resource through the same
        // reader and never sees one. It is still a byte the build added to the recording, and
        // the embedded template is meant to be the recording with the host swapped and nothing
        // else, so the raw bytes are where it has to be caught.
        using var stream = TemplateStream();

        var head = new byte[3];
        stream.ReadExactly(head);

        Assert.False(head is [0xEF, 0xBB, 0xBF], "The build wrote a UTF-8 byte order mark.");
    }

    [Fact]
    public async Task The_placeholder_is_not_something_the_recording_already_said()
    {
        // If the broker ever served this string itself, swapping it back would be lossy and
        // the test above would be checking nothing.
        Assert.DoesNotContain(PlaceholderHost, await RecordingAsync(), StringComparison.Ordinal);
    }
}
