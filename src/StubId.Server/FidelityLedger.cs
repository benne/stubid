using System.Reflection;
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
