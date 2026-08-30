using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Every claim StubID makes about its own fidelity has to be complete and checkable.
/// </summary>
/// <remarks>
/// The annotations are only worth having if they cannot rot. A claim to have verified
/// something against a recording is checked against the recording actually being there, and a
/// deliberate divergence has to say why, because that reason is what a caller is sent to when
/// an unimplemented endpoint answers.
/// </remarks>
public class FidelityLedgerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FidelityLedgerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private static IReadOnlyList<FidelityEntry> Ledger() => FidelityLedger.Read(
        typeof(Tokens).Assembly, typeof(StubId.Wire.JwsWriter).Assembly);

    [Fact]
    public void The_ledger_is_not_empty()
    {
        // A ledger that reads nothing would pass every check below.
        Assert.NotEmpty(Ledger());
    }

    [Fact]
    public void Every_entry_says_enough_to_be_checked()
    {
        var incomplete = Ledger().Where(e => !e.Complete).ToList();

        Assert.True(incomplete.Count == 0,
            "These say nothing that can be checked: "
            + string.Join(", ", incomplete.Select(e => $"{e.Subject} ({e.Provenance})")));
    }

    [Fact]
    public void Anything_claimed_verified_names_a_recording_that_exists()
    {
        // The failure this prevents is a claim of having checked something against a
        // recording that was renamed, moved or never written.
        var missing = Ledger()
            .Where(e => e.Provenance == "VerifiedLive" && e.Evidence is not null)
            .Where(e => !File.Exists(Path.Combine(Root(), e.Evidence!))
                        && !Directory.Exists(Path.Combine(Root(), e.Evidence!)))
            .ToList();

        Assert.True(missing.Count == 0,
            "These cite a recording that is not there: "
            + string.Join(", ", missing.Select(e => $"{e.Subject} -> {e.Evidence}")));
    }

    [Fact]
    public void Every_stated_reason_points_somewhere_real()
    {
        // A divergence's reason is what an unimplemented endpoint sends a caller to, so a
        // reason pointing at a document that does not exist is worse than none.
        var dangling = Ledger()
            .Where(e => e.Reason is not null && e.Reason.Contains('/', StringComparison.Ordinal))
            .Where(e => !File.Exists(Path.Combine(Root(), e.Reason!.Split('#')[0])))
            .ToList();

        Assert.True(dangling.Count == 0,
            "These point at a document that is not there: "
            + string.Join(", ", dangling.Select(e => $"{e.Subject} -> {e.Reason}")));
    }

    [Fact]
    public async Task A_running_instance_can_be_asked_what_it_does_not_reproduce()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/_stubid/v1/fidelity", Ct));

        var entries = document.RootElement.GetProperty("entries");

        Assert.NotEqual(0, entries.GetArrayLength());
        Assert.All(entries.EnumerateArray(), e =>
        {
            Assert.True(e.TryGetProperty("subject", out _));
            Assert.True(e.TryGetProperty("tier", out _));
            Assert.True(e.TryGetProperty("provenance", out _));
        });
    }
}
