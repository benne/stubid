using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The catalog still describes the requests the pack was recorded from.
/// </summary>
/// <remarks>
/// <para>
/// A recording is only evidence of what a broker does if the request that produced it is the
/// request the catalog still makes. Nothing checked that: the catalog and the fixtures could
/// drift apart in a refactor and every assertion downstream would go on passing against bytes
/// obtained by asking a different question.
/// </para>
/// <para>
/// This is deliberately offline. <c>verify</c> proves the same thing by asking the broker
/// again, but it needs the network and a credential, so it cannot be a gate — it is a smoke
/// test somebody runs. This reads two files and runs everywhere, which is what makes it safe to
/// move the harness around underneath the pack.
/// </para>
/// <para>
/// In the collection because it scrubs, and scrubbing reads the machine's settings while three
/// other classes are setting and clearing them.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class RecordedRequestTests
{
    private static IReadOnlyList<CaptureCase> Unattended => CaptureCatalog.For(Broker.NetsEidBroker);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var @case in Unattended)
        {
            data.Add(@case.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_catalog_still_sends_what_the_pack_recorded(string id)
    {
        var @case = Unattended.Single(c => c.Id == id);
        using var recorded = JsonDocument.Parse(
            File.ReadAllText(Repository.Fixture(id, "request.json")));

        var request = recorded.RootElement;

        Assert.Equal(@case.Method, request.GetProperty("method").GetString());

        // Scrubbed, because that is what the writer stored. A case URL holds a placeholder
        // rather than a credential, so this is the identity function in practice - but writing
        // the comparison the way the file was written is what keeps it true if that changes.
        Assert.Equal(Scrubber.Scrub(@case.Url), request.GetProperty("url").GetString());

        Assert.Equal(Body(@case), request.TryGetProperty("body", out var body) ? body.GetString() : null);

        Assert.Equal(
            (@case.Headers?.Keys ?? []).OrderBy(n => n, StringComparer.Ordinal),
            request.GetProperty("headers").EnumerateArray()
                .Select(h => h.GetProperty("name").GetString()!)
                .OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every case has a recording, and every recording has a case.
    /// </summary>
    /// <remarks>
    /// The theory above only visits cases that are in the catalog, so a fixture left behind by a
    /// case somebody deleted would sit in the pack unasserted and be hashed into the manifest as
    /// though it meant something.
    /// </remarks>
    [Fact]
    public void The_pack_holds_a_recording_for_every_case_and_nothing_else()
    {
        var onDisk = Directory.EnumerateDirectories(Repository.NebPreProduction)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            Unattended.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
            onDisk);
    }

    /// <summary>The form as the recorder encodes it, with placeholders left in.</summary>
    /// <remarks>
    /// The recorder builds two bodies and stores this one. Percent-encoding hides a value from a
    /// plain string replace, which is how a real secret reached a fixture the first time this
    /// ran, so the sent body and the stored body are built separately from the same form.
    /// </remarks>
    private static string? Body(CaptureCase @case) => @case.Form is null
        ? null
        : string.Join('&', @case.Form.Select(f =>
            $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
}
