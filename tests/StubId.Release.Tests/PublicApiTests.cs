namespace StubId.Release.Tests;

/// <summary>
/// What the published packages offer a consumer, held to a file somebody has to read.
/// </summary>
/// <remarks>
/// Two files rather than one, because one proves only that somebody ran the update switch.
/// <c>PublicApi.current.txt</c> is what the assemblies say right now and is rewritten whenever
/// they change. <c>PublicApi.shipped.txt</c> is what the last released version published, and is
/// rewritten only when a release is cut. Comparing them is what makes "one release carrying
/// <c>[Obsolete]</c> before removal" a rule the build applies rather than a sentence in a
/// document - it is the Roslyn shipped/unshipped idiom with no package added.
/// <para>
/// The switch is <c>STUBID_UPDATE_API=1</c> and not the <c>STUBID_UPDATE_DOCS</c> the generated
/// tables use, deliberately: rewriting the surface this project promises is a more considered act
/// than regenerating a table from annotations, and a reviewer should be able to see from the diff
/// which one was meant.
/// </para>
/// </remarks>
public class PublicApiTests
{
    private const string Current = "tests/StubId.Release.Tests/PublicApi.current.txt";
    private const string Shipped = "tests/StubId.Release.Tests/PublicApi.shipped.txt";

    /// <summary>
    /// Members that went without a release of notice, and why each was allowed to.
    /// </summary>
    /// <remarks>
    /// A door, because some breaks are decided rather than accidental and the alternative to a
    /// door is a guard people learn to route around. Shaped like <c>SpellingTests.Allowed</c>: an
    /// entry carries its reason, and a second test fails when an entry stops describing a break
    /// that is really there, so an exemption cannot outlive what it excused.
    /// <para>
    /// Empty today, and the release that empties it again is the one that should.
    /// </para>
    /// </remarks>
    private static readonly (string Identity, string Why)[] Allowed = [];


    /// <summary>The committed surface is the one the assemblies actually have.</summary>
    [Fact]
    public void The_baseline_is_what_the_assemblies_say()
    {
        var path = Path.Combine(Repository.Root, Current);
        var composed = PublicSurface.Compose();

        if (Environment.GetEnvironmentVariable("STUBID_UPDATE_API") == "1")
        {
            File.WriteAllText(path, composed);

            return;
        }

        var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(committed == composed,
            "The public surface is not what is committed. If the change is intended, rewrite it:"
            + $"{Environment.NewLine}    STUBID_UPDATE_API=1 dotnet test tests/StubId.Release.Tests"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + Difference(committed, composed));
    }

    /// <summary>What is composed carries no line ending but the one the file uses.</summary>
    /// <remarks>
    /// The same failure this project has already paid for once: a generated file compared
    /// byte-for-byte passed on Linux and failed on Windows, and the message showed two texts that
    /// read identically. Asserting the property means the machine says which character is wrong,
    /// and it fails on every platform rather than only the other one.
    /// </remarks>
    [Fact]
    public void What_is_composed_uses_one_line_ending_on_every_platform()
        => Assert.DoesNotContain('\r', PublicSurface.Compose());

    /// <summary>The surface covers every package that is published, and nothing else.</summary>
    /// <remarks>
    /// Read from the project files rather than restated, so the list in <c>PublicSurface</c> and
    /// the set that reaches nuget.org cannot drift apart. A package added to the solution and
    /// forgotten here would ship with no recorded surface at all, which is the failure this
    /// catches - and the one that would be least visible.
    /// </remarks>
    [Fact]
    public void The_baseline_covers_exactly_the_packages_that_are_published()
    {
        var published = Directory
            .EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.csproj",
                SearchOption.AllDirectories)
            .Where(project => !File.ReadAllText(project)
                .Contains("<IsPackable>false</IsPackable>", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var covered = PublicSurface.Published
            .Select(assembly => assembly.GetName().Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(published, covered);
    }

    /// <summary>
    /// Nothing the last release published has gone without a release of deprecation first.
    /// </summary>
    /// <remarks>
    /// The promise, enforced. A member present in the shipped surface and absent from the current
    /// one had to carry <c>[Obsolete]</c> when it shipped, which is what gives a consumer a
    /// release in which their build warns before it breaks.
    /// <para>
    /// Identity is the member's line with the deprecation marker stripped and its declaring type
    /// carried alongside, so that deprecating a member is not read as removing it and two
    /// same-named members on different types are told apart. A member added and removed inside one
    /// release never enters the shipped file, so it cannot fire here - which is correct: nobody
    /// could have depended on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Nothing_that_shipped_is_gone_without_a_release_of_notice()
    {
        var current = Declarations(Current)
            .Select(entry => entry.Identity)
            .ToHashSet(StringComparer.Ordinal);

        var exempt = Allowed.Select(entry => entry.Identity).ToHashSet(StringComparer.Ordinal);

        var gone = Declarations(Shipped)
            .Where(entry => !current.Contains(entry.Identity))
            .Where(entry => !PublicSurface.IsDeprecated(entry.Line))
            .Where(entry => !exempt.Contains(entry.Identity))
            .Select(entry => entry.Identity)
            .ToList();

        Assert.True(gone.Count == 0,
            "These were published by the last release and are gone, with no release of "
            + $"[Obsolete] in between:{Environment.NewLine}"
            + string.Join(Environment.NewLine, gone)
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "Deprecate them for a release, or record the break in Allowed with the reason.");
    }

    /// <summary>The shipped surface is the one this version is going to publish.</summary>
    /// <remarks>
    /// The release workflow could compare the two files at tag time, but its checks of that shape
    /// sit behind <c>ref_type = tag</c>, so a forgotten rewrite would surface only once a signed
    /// tag was public - which is exactly the hole the notes-file check had. Naming the version in
    /// the file instead moves the failure into the release pull request, where the version bump
    /// happens and somebody is already looking.
    /// </remarks>
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
        var current = Declarations(Current)
            .Select(entry => entry.Identity)
            .ToHashSet(StringComparer.Ordinal);

        var shipped = Declarations(Shipped)
            .Select(entry => entry.Identity)
            .ToHashSet(StringComparer.Ordinal);

        var stale = Allowed
            .Where(entry => current.Contains(entry.Identity) || !shipped.Contains(entry.Identity))
            .Select(entry => entry.Identity)
            .ToList();

        Assert.True(stale.Count == 0,
            "These exemptions no longer excuse anything - the member is back, or was never in the "
            + $"shipped surface to begin with:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>Every member in a baseline, keyed by the type that declares it.</summary>
    private static IEnumerable<(string Identity, string Line)> Declarations(string relative)
    {
        var declaring = "";

        foreach (var line in File.ReadLines(Path.Combine(Repository.Root, relative)))
        {
            if (line.StartsWith("type ", StringComparison.Ordinal))
            {
                declaring = line;
            }
            else if (PublicSurface.IsMember(line))
            {
                yield return (declaring + PublicSurface.Identity(line), line);
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
