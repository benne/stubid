using StubId.Server;

namespace StubId.Release.Tests;

/// <summary>
/// Every fidelity entry belongs to one broker, and the rule that decides which.
/// </summary>
/// <remarks>
/// An instance serves one broker and says what it does not reproduce. Until a second broker was
/// recorded that was the whole ledger, so nothing had to say whose behavior an entry described.
/// Now it does: a Signicat instance listing the first broker's divergences would be answering for
/// a broker it is not, and the first broker's generated reference would carry rows about a surface
/// its readers cannot reach.
/// <para>
/// The rule reads the paths an annotation already carries. An annotation carrying neither is the
/// first broker's, which is the rule rather than an accident. What would be an accident is a path
/// the rule cannot parse, and that is checked where the ledger is read, beside the other checks on
/// what an annotation names.
/// </para>
/// </remarks>
public class LedgerOwnershipTests
{
    [Fact]
    public void Every_entry_belongs_to_a_broker_this_build_serves()
    {
        var strays = FidelityLedger.Read(FidelityLedger.Sources)
            .Select(entry => (entry.Subject, Broker: FidelityLedger.OwnerOf(entry)))
            .Where(entry => !BrokerProfiles.Available.Contains(entry.Broker, StringComparer.Ordinal))
            .ToList();

        Assert.True(strays.Count == 0,
            "These name a broker this build does not serve, which is how a mistyped path reads: "
            + string.Join(", ", strays.Select(s => $"{s.Subject} -> {s.Broker}")));
    }

    /// <summary>
    /// Every entry is served by exactly one broker.
    /// </summary>
    /// <remarks>
    /// Read whole, the ledger is every annotation in the two assemblies. Split across the brokers,
    /// it has to come to the same thing: an entry nobody serves is one a reader can only find by
    /// reading the source, and one two brokers serve is a claim made twice.
    /// </remarks>
    [Fact]
    public void Every_entry_is_served_by_exactly_one_broker()
    {
        var whole = FidelityLedger.Read(FidelityLedger.Sources);
        var split = BrokerProfiles.Available.SelectMany(FidelityLedger.ReadFor).ToList();

        // Ordered by owner as well as by subject. Behavior both brokers show carries one annotation
        // each, so a subject appears twice, and the two lists reach those copies in different
        // orders: whole in the order the assemblies yield them, split a broker at a time.
        Assert.Equal(
            whole.OrderBy(e => e.Subject, StringComparer.Ordinal)
                .ThenBy(FidelityLedger.OwnerOf, StringComparer.Ordinal),
            split.OrderBy(e => e.Subject, StringComparer.Ordinal)
                .ThenBy(FidelityLedger.OwnerOf, StringComparer.Ordinal));
    }

    /// <summary>
    /// A reason decides before evidence.
    /// </summary>
    /// <remarks>
    /// The case is real rather than hypothetical: the second broker's userinfo challenge is
    /// byte-identical to the first broker's, so the entry that explains it on the second broker's
    /// page is the first broker's recording. Read from evidence, it would be filed under the wrong
    /// broker and would argue its case on a page nobody reading that instance would open.
    /// </remarks>
    [Fact]
    public void A_reason_decides_before_the_evidence_it_cites()
    {
        Assert.Equal("signicat", FidelityLedger.OwnerOf(Entry(
            // Written in two halves: a reference to a page and an anchor that do not exist yet is
            // exactly what the anchor sweep over this directory is there to catch.
            reason: "docs/brokers/signicat/divergences.md" + "#the-challenge",
            evidence: "fixtures/neb/pp/CAP-017")));
    }

    [Fact]
    public void Evidence_names_the_broker_where_no_reason_does()
    {
        Assert.Equal("signicat", FidelityLedger.OwnerOf(Entry(
            reason: null, evidence: "fixtures/signicat/sandbox/CAP-001")));

        Assert.Equal("signicat", FidelityLedger.OwnerOf(Entry(
            reason: null, evidence: "The tenant discovery document, fixtures/signicat/sandbox/CAP-001.")));
    }

    /// <summary>
    /// An entry naming no broker is the first broker's.
    /// </summary>
    /// <remarks>
    /// The engine was written against one broker and answers for it alone. An annotation on shared
    /// code that both brokers show carries one entry per broker instead, each naming its own page,
    /// which the attribute allows.
    /// </remarks>
    [Theory]
    [InlineData(null, null)]
    [InlineData("docs/research/signed-requests.md", "OpenID Connect Core, the authentication request")]
    public void An_entry_naming_no_broker_is_the_first_brokers(string? reason, string? evidence)
    {
        Assert.Equal(BrokerProfiles.Default, FidelityLedger.OwnerOf(Entry(reason, evidence)));
    }

    /// <summary>
    /// A broker serves its own entries and no others.
    /// </summary>
    /// <remarks>
    /// With one broker the filter cannot be seen from the outside: its list and the whole ledger
    /// are the same list, so dropping the filter changes nothing a test could read. These two say
    /// it directly, and a broker nobody has annotated anything for is the sharper of them - served
    /// unfiltered, it would answer with the first broker's entire ledger.
    /// </remarks>
    [Fact]
    public void A_broker_with_no_entries_serves_none()
    {
        Assert.Empty(FidelityLedger.ReadFor("nobody"));
    }

    /// <summary>A broker calls itself the name the setting takes for it.</summary>
    /// <remarks>
    /// Two vocabularies meet here, and nothing else makes them one word. An entry is filed by the
    /// key a path names and every check reads the names the setting offers, while an instance
    /// serves the name the loaded profile gives itself. A profile whose id differed would serve an
    /// empty ledger with every test here passing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Brokers))]
    public void A_broker_is_called_the_same_by_the_setting_and_by_its_profile(string broker)
    {
        Assert.Equal(broker, BrokerProfiles.Select(broker).Id.Broker);
    }

    public static TheoryData<string> Brokers() => [.. BrokerProfiles.Available];

    /// <summary>
    /// An entry about a broker's own code is filed under that broker.
    /// </summary>
    /// <remarks>
    /// The rule reads paths, and an annotation that carries none is the first broker's by design -
    /// which means an annotation on the second broker's own code that forgets to name a path is
    /// filed under the first, and every check still passes because the first broker is a broker
    /// this build serves. That happened: the annotation saying which of this broker's paths were
    /// never probed carried only what would settle them, and its row appeared on the other
    /// broker's page. A subject is the declaring type's name, so it says which code the entry is
    /// about.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BrokersWithCodeOfTheirOwn))]
    public void An_entry_about_a_brokers_own_code_is_filed_under_that_broker(string broker)
    {
        // A subject is the declaring type's name, or its full name for a type-level entry, so the
        // broker's own types are found segment by segment rather than by the whole string.
        var name = char.ToUpperInvariant(broker[0]) + broker[1..];

        var about = FidelityLedger.Read(FidelityLedger.Sources)
            .Where(entry => entry.Subject.Split('.').Any(s => s.StartsWith(name, StringComparison.Ordinal)))
            .ToList();

        Assert.True(about.Count > 0,
            $"Nothing in the ledger is about {broker}, so this checks nothing. If its code was "
            + "renamed, rename what this looks for.");

        var misfiled = about
            .Where(entry => FidelityLedger.OwnerOf(entry) != broker)
            .Select(entry => $"{entry.Subject} -> {FidelityLedger.OwnerOf(entry)}")
            .ToList();

        Assert.True(misfiled.Count == 0,
            $"These are about {broker} and are filed elsewhere, so they argue their case on a page "
            + "nobody reading that instance would open: " + string.Join(", ", misfiled));
    }

    /// <summary>
    /// Brokers whose own code this build carries, which is every one but the default.
    /// </summary>
    /// <remarks>
    /// The engine was written against the first broker and is filed under it by the same rule, so
    /// looking for its name would claim every entry the seam has not moved.
    /// </remarks>
    public static TheoryData<string> BrokersWithCodeOfTheirOwn() =>
        [.. BrokerProfiles.Available.Where(broker => broker != BrokerProfiles.Default)];

    private static FidelityEntry Entry(string? reason, string? evidence) =>
        new("Subject.Member", "Exact", "Divergent", evidence, reason, null, true);
}
