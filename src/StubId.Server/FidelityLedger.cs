using System.Reflection;
using StubId.Abstractions;

namespace StubId.Server;

/// <summary>One annotated piece of emulated behaviour, as it appears in the ledger.</summary>
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
/// The annotations live next to the behaviour they describe, so they cannot drift from it the
/// way a separate document would. Reading them back gives three things at once: a build check
/// that every claim is complete, a served endpoint so a running instance can be asked what it
/// does and does not reproduce, and the source for the generated broker reference.
/// </remarks>
public static class FidelityLedger
{
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
