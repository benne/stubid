using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// When a pack may say it was recorded today, and when it may not.
/// </summary>
/// <remarks>
/// The sitting that recorded the session pack happened on one afternoon and cannot be
/// repeated. A later sitting adds a case beside those recordings and rewrites the manifest to
/// cover it, and that manifest's date is the only place the pack states when it was made -
/// nothing in a case's own meta.json carries one.
/// </remarks>
public class ManifestTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string root = Directory.CreateTempSubdirectory("stubid-manifest-").FullName;

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_pack_with_no_manifest_yet_is_stamped_now()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "recorded.json"), "{}", Ct);

        await new FixtureStore(root).WriteManifestKeepingDateAsync(Ct);

        var stamped = DateTimeOffset.Parse(CapturedAt(), styles: System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.True(
            DateTimeOffset.UtcNow - stamped < TimeSpan.FromMinutes(5),
            $"A first write should stamp now, and stamped {stamped:O}.");
    }

    [Fact]
    public async Task A_later_write_keeps_the_date_the_pack_already_carries()
    {
        // The failure this exists for: finishing a sitting that recorded one case would
        // otherwise relabel every recording beside it as made that day.
        const string theSitting = "2026-08-30T22:44:49Z";
        var store = new FixtureStore(root);

        await File.WriteAllTextAsync(Path.Combine(root, "recorded.json"), "{}", Ct);
        await store.WriteManifestAsync(theSitting, Ct);

        await File.WriteAllTextAsync(Path.Combine(root, "recorded-later.json"), "{}", Ct);
        await store.WriteManifestKeepingDateAsync(Ct);

        Assert.Equal(theSitting, CapturedAt());

        // The date is kept, not the file list: the point of rewriting is to cover the new one.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "MANIFEST.json")));
        Assert.Equal(2, manifest.RootElement.GetProperty("files").EnumerateObject().Count());
    }

    [Fact]
    public async Task Rewriting_a_pack_that_has_not_changed_changes_nothing()
    {
        // What `sanitise` promises in its own remarks, and could not deliver while it stamped
        // a fresh date over an unchanged directory.
        var store = new FixtureStore(root);
        await File.WriteAllTextAsync(Path.Combine(root, "recorded.json"), "{}", Ct);

        await store.WriteManifestKeepingDateAsync(Ct);
        var first = await File.ReadAllTextAsync(Path.Combine(root, "MANIFEST.json"), Ct);

        await store.WriteManifestKeepingDateAsync(Ct);
        var again = await File.ReadAllTextAsync(Path.Combine(root, "MANIFEST.json"), Ct);

        Assert.Equal(first, again);
    }

    private string CapturedAt()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "MANIFEST.json")));
        return manifest.RootElement.GetProperty("capturedAtUtc").GetString()!;
    }
}
