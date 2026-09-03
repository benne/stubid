namespace StubId.Fixtures.Tests;

/// <summary>
/// The tests that reach for real environment variables, kept out of each other's way.
/// </summary>
/// <remarks>
/// <para>
/// Three classes used to set <c>STUBID_NEB_PP_CLIENT_ID</c> and its secret around an action and
/// restore them afterwards, each with its own copy of the idiom. Environment variables belong to
/// the process, xUnit runs test classes in parallel, and so one class's restore ran inside
/// another class's action: the one holding the credential then threw "Set
/// STUBID_NEB_PP_CLIENT_ID to record with the private client" from code that had just set it.
/// </para>
/// <para>
/// It went unseen for a long time because a developer machine hides it. The environment is only
/// consulted first; <c>capture.local.json</c> answers underneath, so a cleared variable still
/// resolves and the race has no visible effect. CI has no such file, which is where it surfaced -
/// on Windows, on a change that had nothing to do with any of this.
/// </para>
/// <para>
/// It is not a narrow window, either, which is why re-running does not settle anything.
/// Tracing the enter and exit of this helper showed all three classes on three different
/// threads, with <c>ScrubberTests</c> and <c>RequestObjectTests</c> each setting and clearing
/// the variables entirely inside one <c>StagingWriteTests</c> action - every run. What varies is
/// only whether the one read lands in a cleared moment, which is a fraction of the window and
/// explains a failure that appears once in twenty on one platform and never on another.
/// </para>
/// <para>
/// Joining one collection is what fixes it: xUnit runs the classes in a collection one after
/// another, and the same trace then shows one thread and no overlapping windows at all. Sharing
/// the helper is what keeps it fixed, because three copies is how the second and third came to
/// be written without anyone noticing the first.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ProcessEnvironment
{
    public const string Name = "the process environment";

    /// <summary>
    /// Runs an action with settings in place, and puts back whatever was there before.
    /// </summary>
    /// <remarks>
    /// The environment wins over <c>capture.local.json</c>, so a test written this way is
    /// deterministic on a machine that has real credentials and on one that has none, which is
    /// what CI is. That only holds while nothing else is writing the same variables, which is
    /// what the collection is for.
    /// </remarks>
    public static T With<T>(Func<T> act, params (string Name, string Value)[] settings)
    {
        var previous = settings.Select(s => Environment.GetEnvironmentVariable(s.Name)).ToArray();

        foreach (var (name, value) in settings)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            return act();
        }
        finally
        {
            for (var i = 0; i < settings.Length; i++)
            {
                Environment.SetEnvironmentVariable(settings[i].Name, previous[i]);
            }
        }
    }

    /// <summary>The same, for an action with nothing to return.</summary>
    public static void With(Action act, params (string Name, string Value)[] settings) =>
        With<object?>(() => { act(); return null; }, settings);
}
