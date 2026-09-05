using System.Collections.Concurrent;
using StubId.Abstractions;

namespace StubId.Server;

/// <summary>
/// The CPR-match flow: how a private service provider learns whether the person who just
/// signed in holds the personal number it already has.
/// </summary>
/// <remarks>
/// <para>
/// A private service provider cannot ask for the <c>ssn</c> scope, so it never receives a
/// personal number. It submits one it already holds and is told yes or no. The limit is three
/// attempts per session, which stops the endpoint being used to enumerate.
/// </para>
/// <para>
/// The limit is behaviour, not configuration: a test that exhausts it is testing something
/// real, and an emulator without it would let a suite pass while the same code fails against
/// the broker on the fourth call.
/// </para>
/// </remarks>
public sealed class CprMatch(TimeProvider clock)
{
    /// <summary>Attempts and when the window they belong to opened.</summary>
    private readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset Opened)> _sessions =
        new(StringComparer.Ordinal);

    public const int Allowed = 3;

    /// <summary>
    /// The window the attempts are counted in. Fifteen minutes, from the broker's own
    /// documentation rather than from a recording: exhausting the limit needs a completed
    /// login, and the sitting that could have recorded it spent its attempts elsewhere.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The refusal, spelled the way the broker spells it.</summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.DocsConfirmed,
        Evidence = "Unrecorded: reaching it needs a fourth call inside one authenticated session.")]
    public const string Exceeded = "Cpr Match exceeded. Only 3 tries is allowed within a session.";

    /// <summary>
    /// Counts an attempt and says whether it is allowed. The window opens on the first
    /// attempt, so a session that goes quiet for fifteen minutes starts again.
    /// </summary>
    public bool TryAttempt(string sessionId)
    {
        var now = clock.GetUtcNow();

        var state = _sessions.AddOrUpdate(
            sessionId,
            _ => (1, now),
            (_, existing) => now - existing.Opened >= Window
                ? (1, now)
                : (existing.Attempts + 1, existing.Opened));

        return state.Attempts <= Allowed;
    }

    public void Forget(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Drops every session's attempts, for a reset.
    /// </summary>
    /// <remarks>
    /// Counted per session, so a reset that cleared the sessions and left these behind kept
    /// attempts against logins that no longer existed. Nothing could reach them again, but the
    /// instance was not as fresh as it said it was.
    /// </remarks>
    public void Clear() => _sessions.Clear();
}
