using StubId.InProcess;

namespace StubId.Release.Tests;

/// <summary>
/// The control API's own field names, held to a file, because nothing else held them.
/// </summary>
/// <remarks>
/// The .NET surface has had a baseline since 2026.09.3 and the JSON one had nothing. Field names
/// are derived from property names by a naming policy and almost every test reads them back
/// through the typed client's own records, so renaming a property renamed both sides at once and
/// the suite stayed green - while a caller written in anything but .NET broke. That gap was
/// written down in <c>docs/compatibility.md</c> under what is deliberately not enforced; this is
/// the entry being removed from that list.
/// <para>
/// Two files and one update switch, the same shape <c>PublicApiTests</c> uses, for the same
/// reason: one file proves only that somebody ran the switch, and the promise is about what the
/// last release published.
/// </para>
/// </remarks>
public class ControlSurfaceTests
{
    private const string Current = "tests/StubId.Release.Tests/ControlApi.current.txt";
    private const string Shipped = "tests/StubId.Release.Tests/ControlApi.shipped.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Routes whose fields went without a release of notice, and why each was allowed to.
    /// </summary>
    /// <remarks>
    /// The same door <c>PublicApiTests.Allowed</c> opens, and shut for the same reason: a guard
    /// with no way to record a deliberate break is one people route around. Empty today.
    /// </remarks>
    private static readonly (string Path, string Why)[] Allowed = [];

    /// <summary>The committed shapes are the ones the server actually sends.</summary>
    [Fact]
    public async Task The_baseline_is_what_the_server_sends()
    {
        var path = Path.Combine(Repository.Root, Current);
        var composed = await ControlSurface.ComposeAsync(Ct);

        if (Environment.GetEnvironmentVariable("STUBID_UPDATE_API") == "1")
        {
            await File.WriteAllTextAsync(path, composed, Ct);

            return;
        }

        var committed = (await File.ReadAllTextAsync(path, Ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(committed == composed,
            "The control API is not sending what is committed. If the change is intended, rewrite it:"
            + $"{Environment.NewLine}    STUBID_UPDATE_API=1 dotnet test tests/StubId.Release.Tests"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + Difference(committed, composed));
    }

    /// <summary>What is composed carries no line ending but the one the file uses.</summary>
    [Fact]
    public async Task What_is_composed_uses_one_line_ending_on_every_platform()
        => Assert.DoesNotContain('\r', await ControlSurface.ComposeAsync(Ct));

    /// <summary>Every route this build registers is one the script actually reached.</summary>
    /// <remarks>
    /// The half that makes the file mean something. Without it a route could be added, never
    /// driven, and recorded nowhere - and the baseline would say nothing while looking complete.
    /// Read from the router rather than from a list beside it, so the two cannot drift.
    /// </remarks>
    [Fact]
    public async Task Every_route_that_is_registered_is_one_the_baseline_reached()
    {
        await using var stub = new StubIdHostBuilder().WithKeyPath(ControlSurface.KeyPath).Build();
        await stub.StartAsync(Ct);

        var recorded = Routes(Current);

        var unreached = ControlSurface.Registered(stub)
            .Where(route => !recorded.Contains((route.Method, route.Pattern)))
            .Select(route => $"{route.Method} {route.Pattern}")
            .ToList();

        Assert.True(unreached.Count == 0,
            "These routes are registered and the baseline never reached them, so nothing records "
            + $"what they answer:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unreached));
    }

    /// <summary>And every route in the file is one this build still registers.</summary>
    /// <remarks>
    /// The other direction, and the one that catches a removal. A route that stops being
    /// registered stops appearing here, which is what a caller finds out as a 404.
    /// </remarks>
    [Fact]
    public async Task Every_route_in_the_baseline_is_one_this_build_still_registers()
    {
        await using var stub = new StubIdHostBuilder().WithKeyPath(ControlSurface.KeyPath).Build();
        await stub.StartAsync(Ct);

        var registered = ControlSurface.Registered(stub)
            .Select(route => (route.Method, route.Pattern))
            .ToHashSet();

        var gone = Routes(Current)
            .Where(route => !registered.Contains(route))
            .Select(route => $"{route.Method} {route.Pattern}")
            .ToList();

        Assert.True(gone.Count == 0,
            "The baseline records these and this build does not register them: "
            + string.Join(", ", gone));
    }

    /// <summary>
    /// Nothing the last release answered with has gone without a release of deprecation first.
    /// </summary>
    /// <remarks>
    /// The promise, enforced on the JSON half. A field the last release sent and this one does not
    /// is a break for anybody reading the bytes, and the only thing that makes it not one is the
    /// route having said it was going away - which it says with a <c>Deprecation</c> header and
    /// the endpoint metadata behind it.
    /// <para>
    /// Keyed on the whole line, so a field moving between routes is not read as a field surviving.
    /// A field added and removed inside one release never enters the shipped file and so cannot
    /// fire here, which is correct: nobody could have depended on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Nothing_that_shipped_is_gone_without_a_release_of_notice()
    {
        var deprecated = Deprecated(Shipped);
        var current = Recorded(Current).Select(entry => entry.Line).ToHashSet(StringComparer.Ordinal);
        var exempt = Allowed.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);

        var gone = Recorded(Shipped)
            .Where(entry => !current.Contains(entry.Line))
            .Where(entry => !deprecated.Contains(entry.Route))
            .Where(entry => !exempt.Contains(entry.Line))
            .Select(entry => entry.Line)
            .ToList();

        Assert.True(gone.Count == 0,
            "These were answered by the last release and are gone, with no release of notice on "
            + $"the route that carried them:{Environment.NewLine}"
            + string.Join(Environment.NewLine, gone)
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "Deprecate the route for a release, or record the break in Allowed with the reason.");
    }

    /// <summary>The shipped surface is the one this version is going to publish.</summary>
    [Fact]
    public void The_shipped_baseline_names_this_version()
    {
        var first = File.ReadLines(Path.Combine(Repository.Root, Shipped)).First();

        Assert.Equal($"# shipped {PublicSurface.Version()}", first);
    }

    /// <summary>Every exemption still describes a break that is really there.</summary>
    [Fact]
    public void Every_exemption_still_describes_a_break_that_happened()
    {
        var current = Recorded(Current).Select(entry => entry.Line).ToHashSet(StringComparer.Ordinal);
        var shipped = Recorded(Shipped).Select(entry => entry.Line).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed
            .Where(entry => current.Contains(entry.Path) || !shipped.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToList();

        Assert.True(stale.Count == 0,
            "These exemptions no longer excuse anything - the field is back, or was never in the "
            + $"shipped surface to begin with:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>The routes a baseline records as having said they were going away.</summary>
    private static HashSet<(string, string)> Deprecated(string relative) =>
        File.ReadLines(Path.Combine(Repository.Root, relative))
            .Where(line => line.Contains(" -> ", StringComparison.Ordinal)
                           && line.EndsWith(ControlSurface.DeprecatedMark, StringComparison.Ordinal))
            .Select(line => line.Split(' '))
            .Select(words => (words[0], words[1]))
            .ToHashSet();

    /// <summary>Every route a baseline records an answer for, bodyless ones included.</summary>
    /// <remarks>
    /// Read from the header lines rather than from the fields under them, because a route that
    /// answers with no body has no fields and would otherwise read as one nothing ever reached -
    /// which is exactly the six that the completeness rule exists to notice.
    /// </remarks>
    private static HashSet<(string Method, string Pattern)> Routes(string relative) =>
        File.ReadLines(Path.Combine(Repository.Root, relative))
            .Where(line => line.Contains(" -> ", StringComparison.Ordinal))
            .Select(line => line.Split(' '))
            .Select(words => (words[0], words[1]))
            .ToHashSet();

    /// <summary>Every field in a baseline, carrying the route whose response holds it.</summary>
    private static IEnumerable<(string Line, (string Method, string Pattern) Route)> Recorded(
        string relative = Current)
    {
        var method = "";
        var pattern = "";

        foreach (var line in File.ReadLines(Path.Combine(Repository.Root, relative)))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                yield return ($"{method} {pattern}{line[1..]}", (method, pattern));
            }
            else if (line.Contains(" -> ", StringComparison.Ordinal))
            {
                var words = line.Split(' ');

                method = words[0];
                pattern = words[1];
            }
        }
    }

    /// <summary>The first few lines that differ, which is what a reader needs to see.</summary>
    private static string Difference(string committed, string composed)
    {
        var was = committed.Split('\n');
        var now = composed.Split('\n');

        var changed = Enumerable
            .Range(0, Math.Max(was.Length, now.Length))
            .Where(i => (i < was.Length ? was[i] : null) != (i < now.Length ? now[i] : null))
            .Take(20)
            .Select(i => $"  line {i + 1}:{Environment.NewLine}"
                + $"    committed: {(i < was.Length ? was[i] : "<end of file>")}{Environment.NewLine}"
                + $"    composed:  {(i < now.Length ? now[i] : "<end of file>")}");

        return string.Join(Environment.NewLine, changed);
    }
}
