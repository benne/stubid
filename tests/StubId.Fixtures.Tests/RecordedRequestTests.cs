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
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var target in BrokerTarget.All)
        {
            foreach (var @case in CaptureCatalog.For(target.Broker))
            {
                data.Add(target.Key, @case.Id);
            }
        }

        return data;
    }

    public static TheoryData<string> Brokers()
    {
        var data = new TheoryData<string>();
        foreach (var target in BrokerTarget.All)
        {
            data.Add(target.Key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_catalog_still_sends_what_the_pack_recorded(string broker, string id)
    {
        var target = BrokerTarget.Select(broker);
        var @case = CaptureCatalog.For(target.Broker).Single(c => c.Id == id);
        using var recorded = JsonDocument.Parse(
            File.ReadAllText(Repository.Fixture(target, id, "request.json")));

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
    /// And the meta beside a recording still describes the case that recorded it.
    /// </summary>
    /// <remarks>
    /// A <c>meta.json</c> is a copy of the case as it stood when the case was last recorded, so
    /// editing a case without re-recording it leaves a committed file claiming something the
    /// recording beside it contradicts. That was found by hand once, in the first broker's pack,
    /// and nothing was left watching for the next one: the classification theory deliberately
    /// asserts against the catalog, because the case is the live claim and the meta is the copy -
    /// which leaves the copy compared with nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void The_meta_still_describes_the_case_that_recorded_it(string broker, string id)
    {
        var target = BrokerTarget.Select(broker);
        var @case = CaptureCatalog.For(target.Broker).Single(c => c.Id == id);
        using var recorded = JsonDocument.Parse(
            File.ReadAllText(Repository.Fixture(target, id, "meta.json")));

        var meta = recorded.RootElement;

        Assert.Equal(@case.Description, meta.GetProperty("description").GetString());
        Assert.Equal(@case.Settles, meta.GetProperty("settles").GetString());
        Assert.Equal(@case.Expected.ToString(), meta.GetProperty("disposition").GetString());
    }

    /// <summary>
    /// Every case has a recording, and every recording has a case.
    /// </summary>
    /// <remarks>
    /// The theory above only visits cases that are in the catalog, so a fixture left behind by a
    /// case somebody deleted would sit in the pack unasserted and be hashed into the manifest as
    /// though it meant something.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Brokers))]
    public void The_pack_holds_a_recording_for_every_case_and_nothing_else(string broker)
    {
        var target = BrokerTarget.Select(broker);
        var onDisk = Directory.EnumerateDirectories(Path.Combine(Repository.Root, target.Pack))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            CaptureCatalog.For(target.Broker).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
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
