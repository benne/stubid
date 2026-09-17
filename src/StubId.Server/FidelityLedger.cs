using System.Reflection;
using System.Text.RegularExpressions;
using StubId.Abstractions;

namespace StubId.Server;

/// <summary>One annotated piece of emulated behavior, as it appears in the ledger.</summary>
public sealed record FidelityEntry(
    string Subject,
    string Tier,
    string Provenance,
    string? Evidence,
    string? Reason,
    string? AwaitingCapture,
    bool Complete);

/// <summary>
/// Everything StubID has said about its own fidelity, collected from the code that emits it.
/// </summary>
/// <remarks>
/// The annotations live next to the behavior they describe, so they cannot drift from it the
/// way a separate document would. Reading them back gives three things at once: a build check
/// that every claim is complete, a served endpoint so a running instance can be asked what it
/// does and does not reproduce, and the source for the generated broker reference.
/// </remarks>
public static class FidelityLedger
{
    /// <summary>
    /// The assemblies whose annotations make up StubID's own ledger.
    /// </summary>
    /// <remarks>
    /// One list because there were four, written out identically in the control API, the admin
    /// page and two test projects. A fifth assembly annotated tomorrow would have had to be
    /// remembered in all of them, and the one that forgot would have served a shorter ledger
    /// than the others without saying so.
    /// </remarks>
    public static Assembly[] Sources => [typeof(Tokens).Assembly, typeof(Wire.JwsWriter).Assembly];

    /// <summary>
    /// What a reader needs to know about first, which is the opposite of alphabetical.
    /// </summary>
    /// <remarks>
    /// What is not reproduced at all, then what diverges on purpose, then what rests on
    /// documentation, and only then what a recording confirmed. Shared so the admin page and the
    /// generated reference put things in the same order, because two orderings of one list is a
    /// question a reader should never have to ask.
    /// </remarks>
    public static int ProvenanceWeight(string provenance) => provenance switch
    {
        "NotEmulated" => 0,
        "Divergent" => 1,
        "DocsConflict" => 2,
        "Assumed" => 3,
        "DocsConfirmed" => 4,
        _ => 5,
    };

    /// <summary>
    /// Which broker an entry describes.
    /// </summary>
    /// <remarks>
    /// The annotations never say, and until a second broker was recorded they did not have to: one
    /// instance served one broker, and the ledger was all of it. They do carry paths, though, and a
    /// path names a broker - a reason points into <c>docs/brokers/&lt;key&gt;/</c> and evidence into
    /// <c>fixtures/&lt;key&gt;/</c> - so the entry already says whose behavior it explains.
    /// <para>
    /// The reason decides first, because a divergence belongs to the broker whose page argues it,
    /// whatever recording it cites. A divergence argued on one broker's page may cite the other's
    /// recording to show where the two agree, or cite both; read from evidence it would be filed
    /// wherever the first path happened to point.
    /// </para>
    /// <para>
    /// An entry naming neither is the first broker's, because the engine was written against it and
    /// answers for it alone until a recording moves something behind the seam. Behavior both brokers
    /// show says so twice instead: <see cref="FidelityAttribute" /> may be applied more than once,
    /// so shared code carries one annotation per broker, each pointing at that broker's page.
    /// </para>
    /// </remarks>
    public static string OwnerOf(FidelityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Named(entry.Reason, "docs/brokers")
            ?? Named(entry.Evidence, "fixtures")
            ?? BrokerProfiles.Default;
    }

    /// <summary>The ledger as one broker's instance should serve it.</summary>
    /// <remarks>
    /// An instance serves one broker, and a reader asking what it does not reproduce is asking
    /// about that one. Served whole, the list answered for a broker the instance is not.
    /// </remarks>
    public static IReadOnlyList<FidelityEntry> ReadFor(string broker) =>
        [.. Read(Sources).Where(e => string.Equals(OwnerOf(e), broker, StringComparison.Ordinal))];

    /// <summary>The key a path names under a directory, or null where it names none.</summary>
    /// <remarks>
    /// Evidence may hold several paths, and prose around them. The first one that sits under the
    /// directory decides, which is why evidence naming a second broker's recording has to be a
    /// reason instead.
    /// </remarks>
    private static string? Named(string? value, string directory) => value is not null
        && Regex.Match(value, $@"(?:^|[\s,(]){Regex.Escape(directory)}/(?<key>[a-z0-9-]+)/")
            is { Success: true } named
        ? named.Groups["key"].Value
        : null;

    /// <summary>A path this cannot read, which <see cref="OwnerOf" /> would file under the default.</summary>
    /// <remarks>
    /// <see cref="Named" /> answers null for a value carrying no path and for one carrying a path it
    /// cannot parse, and <see cref="OwnerOf" /> cannot tell those apart. The first is the rule; the
    /// second is one broker's entry served on another's ledger with nothing saying so. A check over
    /// the annotations reads this, so an unreadable path fails rather than defaults.
    /// </remarks>
    internal static bool NamesUnreadably(string? value, string directory) =>
        value is not null
        && value.Contains(directory + "/", StringComparison.Ordinal)
        && Named(value, directory) is null;

    public static IReadOnlyList<FidelityEntry> Read(params Assembly[] assemblies) =>
        [.. assemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(Annotated)
            .OrderBy(e => e.Subject, StringComparer.Ordinal)];

    private static IEnumerable<FidelityEntry> Annotated(Type type)
    {
        foreach (var entry in Entries(type, type.FullName ?? type.Name))
        {
            yield return entry;
        }

        var members = type
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        foreach (var member in members)
        {
            foreach (var entry in Entries(member, $"{type.Name}.{member.Name}"))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<FidelityEntry> Entries(MemberInfo member, string subject) =>
        member.GetCustomAttributes<FidelityAttribute>().Select(a => new FidelityEntry(
            subject,
            a.Tier.ToString(),
            a.Provenance.ToString(),
            a.Evidence,
            a.Reason,
            a.AwaitingCapture,
            a.IsComplete));
}
