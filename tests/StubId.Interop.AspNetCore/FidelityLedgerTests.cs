using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StubId.Profiles;
using StubId.Server;

// Both assemblies declare a FidelityEntry - the server's is what the ledger reads, the
// client's is what a consumer meets - so the client is reached by alias rather than by import.
using StubIdClient = StubId.Client.StubIdClient;

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

    private static IReadOnlyList<FidelityEntry> Ledger() =>
        FidelityLedger.Read(FidelityLedger.Sources);

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
        // One behavior is often settled by several recordings together, so evidence may name
        // more than one and every one of them has to exist.
        var missing = Ledger()
            .Where(e => e.Provenance == "VerifiedLive" && e.Evidence is not null)
            .SelectMany(e => e.Evidence!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(cited => !File.Exists(Path.Combine(Root(), cited))
                                && !Directory.Exists(Path.Combine(Root(), cited)))
                .Select(cited => $"{e.Subject} -> {cited}"))
            .ToList();

        Assert.True(missing.Count == 0,
            "These cite a recording that is not there: " + string.Join(", ", missing));
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

    /// <summary>The recording is named on the wire, not only in the source.</summary>
    /// <remarks>
    /// Read as bytes rather than through the typed client, because the field names are what a
    /// caller that is not .NET has to know and nothing else pins them. The version is checked
    /// against the profile the host actually loaded rather than against a literal, which would
    /// be a second place to update and the first one somebody forgets.
    /// </remarks>
    [Fact]
    public async Task A_running_instance_says_which_recording_it_serves()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/_stubid/v1/fidelity", Ct));

        var profile = document.RootElement.GetProperty("profile");
        var loaded = _factory.Services.GetRequiredService<IBrokerProfile>().Id;

        Assert.Equal(loaded.Broker, profile.GetProperty("broker").GetString());
        Assert.Equal(loaded.Version, profile.GetProperty("version").GetString());
    }

    /// <summary>And the typed client reads it, rather than leaving it to be parsed by hand.</summary>
    [Fact]
    public async Task The_client_reads_the_recording_off_the_wire()
    {
        using var http = _factory.CreateClient();
        using var stubId = new StubIdClient(http);

        var profile = await stubId.ProfileAsync(Ct);
        var loaded = _factory.Services.GetRequiredService<IBrokerProfile>().Id;

        Assert.NotNull(profile);
        Assert.Equal(loaded.Broker, profile.Broker);
        Assert.Equal(loaded.Version, profile.Version);
        Assert.Equal(loaded.ToString(), profile.ToString());
    }

    /// <summary>
    /// It says where its surface sits, too, so a caller can build an authority without knowing
    /// which broker it asked for.
    /// </summary>
    /// <remarks>
    /// The Testcontainers module reads exactly this to answer <c>Authority</c>. Asserted against
    /// the loaded profile rather than against <c>op</c>, because a literal here would agree with
    /// a literal in the module and neither would be checking anything.
    /// </remarks>
    [Fact]
    public async Task A_running_instance_says_where_its_surface_sits()
    {
        using var http = _factory.CreateClient();
        using var stubId = new StubIdClient(http);
        using var document = JsonDocument.Parse(
            await http.GetStringAsync("/_stubid/v1/fidelity", Ct));

        var loaded = _factory.Services.GetRequiredService<IBrokerProfile>().Root;
        var profile = await stubId.ProfileAsync(Ct);

        Assert.Equal(
            loaded.Segments,
            document.RootElement.GetProperty("profile").GetProperty("root").GetString());

        Assert.NotNull(profile);
        Assert.Equal(loaded.Segments, profile.Root);
    }

    /// <summary>
    /// An instance that names a recording but not a root reads as no root, which is not the same
    /// as a root of nothing.
    /// </summary>
    /// <remarks>
    /// The published image of the release before this one answers exactly this body. A broker
    /// served at the host root reports the empty string, so a missing field deserializing to one
    /// would have the module hand a caller the bare address as an authority - correct for a
    /// broker that does not exist yet, and wrong for every instance that has ever run.
    /// </remarks>
    [Fact]
    public async Task An_instance_that_names_no_root_is_not_read_as_the_host_root()
    {
        const string Body =
            """{"profile":{"broker":"neb","version":"2026.09.1"},"entries":[]}""";

        using var http = new HttpClient(new OneBody(Body))
        {
            BaseAddress = new Uri("http://stubid.invalid"),
        };

        using var stubId = new StubIdClient(http);

        var profile = await stubId.ProfileAsync(Ct);

        Assert.NotNull(profile);
        Assert.Equal("neb", profile.Broker);
        Assert.Null(profile.Root);
    }

    /// <summary>An instance too old to answer is a null, not an exception.</summary>
    /// <remarks>
    /// The case the client is written for and the one no real server here can produce: the
    /// package and the image are versioned separately, so a suite can hold a client newer than
    /// the instance it drives. Served by hand for that reason - what is under test is the
    /// deserializer meeting a body with the key missing.
    /// </remarks>
    [Fact]
    public async Task An_instance_that_predates_the_field_reads_as_no_recording()
    {
        using var http = new HttpClient(new OneBody("""{"entries":[]}"""))
        {
            BaseAddress = new Uri("http://stubid.invalid"),
        };

        using var stubId = new StubIdClient(http);

        Assert.Null(await stubId.ProfileAsync(Ct));
    }

    /// <summary>A fidelity route answering exactly one body, for the older-instance case.</summary>
    private sealed class OneBody(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
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
