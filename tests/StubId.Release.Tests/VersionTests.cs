using System.Reflection;
using System.Text.RegularExpressions;
using StubId.Server;
using StubId.Testing;

namespace StubId.Release.Tests;

/// <summary>
/// What the build says its version is, and everything else in the tree that names one.
/// </summary>
/// <remarks>
/// A release is the one change that has to agree with itself across a build property, a
/// compiled constant and the documentation, while a profile identifier deliberately does not
/// follow. Nothing in the language holds any of that together, and before this project existed
/// it did not hold: one image had three tags at once and the profile named a month whose
/// recordings it no longer matched.
/// </remarks>
public class VersionTests
{
    /// <summary>
    /// The version as written, read back off the shipped assembly rather than out of
    /// Directory.Build.props.
    /// </summary>
    /// <remarks>
    /// Reading the assembly proves the evaluated property reached the artifact. Reading the
    /// property file would only prove someone typed it there, which is the half that was never
    /// in doubt. The informational version keeps the value verbatim, padding included, and
    /// gains a <c>+commit</c> suffix once SourceLink is active.
    /// </remarks>
    private static string Declared()
    {
        var informational = typeof(StubIdBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        return informational.Split('+')[0];
    }

    /// <summary>The build and the container tag the module pulls are one string.</summary>
    /// <remarks>
    /// The image is built from the commit that carries this version and tagged with it, so a
    /// module naming anything else would pull a StubID that was never published beside the
    /// package doing the pulling.
    /// </remarks>
    [Fact]
    public void The_version_the_assembly_carries_is_the_image_the_module_names()
    {
        // The tag rather than the whole reference, so this file carries no image literal of its
        // own for the sweep below to find. What the reference should be is pinned where it
        // belongs, by StubIdBuilderTests.
        Assert.Equal(Declared(), StubIdBuilder.StubIdImage.Split(':')[^1]);
    }

    /// <summary>Which recording is being served, which is not which build is shipped.</summary>
    /// <remarks>
    /// These were one string until 2026.09.2, because every release before it carried a
    /// sitting. That one built an admin interface and fixed a race and recorded nothing, so the
    /// profile stayed where it was. A profile's version answers which recording of the broker
    /// is served; the build's answers which StubID is shipped, and the two only move together
    /// when a release carries a capture.
    /// <para>
    /// So the recording is pinned here rather than derived. Moving the constant is a claim that
    /// a sitting was taken and its fixtures are in the tree; moving it for any other reason
    /// makes the profile version a lie, which is the one thing it cannot afford to be. The
    /// profile may lag the build indefinitely and may never lead it, because a recording cannot
    /// reach anyone before the release that carries it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_profile_names_the_last_recording_and_never_runs_ahead_of_the_build()
    {
        var recorded = new NetsEidBrokerProfile().Id.Version;
        var declared = Declared();

        Assert.Equal("2026.09.1", recorded);
        Assert.True(
            Version.Parse(recorded) <= Version.Parse(declared),
            $"The profile serves {recorded}, which is ahead of the build's {declared}. A "
            + "recording cannot ship before the release that carries it.");
    }

    /// <summary>
    /// The same version in its two published forms, which is why the guides name both.
    /// </summary>
    /// <remarks>
    /// A container tag sorts as text, where 2026.09 precedes 2026.10 and 2026.9 does not, so
    /// the tag keeps its padding. NuGet reads a version as numbers and normalizes the zero
    /// away. Neither is wrong; a reader shown only one of them is, which is what makes this a
    /// documented fact rather than an implementation detail. The assembly version is the same
    /// normalization the package version gets, applied by the SDK where a test can see it.
    /// </remarks>
    [Fact]
    public void The_version_NuGet_publishes_is_the_declared_one_without_its_leading_zeros()
    {
        var normalized = string.Join('.', Declared().Split('.').Select(int.Parse));
        var assembly = typeof(StubIdBuilder).Assembly.GetName().Version!;

        Assert.Equal(normalized, $"{assembly.Major}.{assembly.Minor}.{assembly.Build}");
    }

    /// <summary>Every image tag a reader could copy out of the tree names this version.</summary>
    /// <remarks>
    /// The state this replaces: the module and its test pinned one tag, the compose sample
    /// named <c>latest</c>, three documented <c>docker run</c> lines carried no tag at all, and
    /// the profile named a fourth thing. Following two guides got you two different images.
    /// An unpinned reference and <c>latest</c> stay legal - they resolve to the same manifest -
    /// but a pinned tag that is not this version does not.
    /// </remarks>
    [Fact]
    public void Every_pinned_image_tag_in_the_tree_is_this_version()
    {
        var declared = Declared();
        var image = new Regex(@"ghcr\.io/benne/stubid(?::([^\s""')]+))?");
        List<string> wrong = [];

        foreach (var (relative, full) in Scanned())
        {
            foreach (Match match in image.Matches(File.ReadAllText(full)))
            {
                var tag = match.Groups[1].Success ? match.Groups[1].Value : "latest";

                if (tag != "latest" && tag != declared)
                {
                    wrong.Add($"{relative}: {match.Value}");
                }
            }
        }

        Assert.True(wrong.Count == 0,
            $"Expected every pinned tag to be {declared}. Found:{Environment.NewLine}"
            + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The notes for this version exist, and the landing page points a reader at them.
    /// </summary>
    /// <remarks>
    /// Two things that only ever break while a release is being cut. The release workflow
    /// requires <c>docs/releases/&lt;version&gt;.md</c> to exist, but that check sits behind
    /// <c>ref_type = tag</c>, so the dry run skips it and a missing or misnamed file surfaces
    /// only once a signed tag is public. And one line on <c>docs/index.md</c> sends a reader to
    /// what changed, which has to be the newest notes rather than whichever release was current
    /// when the line was written - nothing else catches that, because a link to an older note
    /// still resolves and every note stays in the table of contents forever.
    /// </remarks>
    [Fact]
    public void The_notes_for_this_version_exist_and_the_landing_page_names_them()
    {
        var declared = Declared();

        Assert.True(
            File.Exists(Path.Combine(Repository.Root, "docs", "releases", $"{declared}.md")),
            $"docs/releases/{declared}.md is not there. The release workflow requires it, but "
            + "only once the tag is pushed.");

        Assert.Contains(
            $"releases/{declared}.md",
            File.ReadAllText(Path.Combine(Repository.Root, "docs", "index.md")),
            StringComparison.Ordinal);
    }

    /// <summary>Every package the documentation tells a reader to install is one we publish.</summary>
    /// <remarks>
    /// The guides named three packages as things a reader uses and gave no way to obtain any of
    /// them, so this is new ground rather than a regression guard. What it catches is a
    /// mistyped identifier, a line naming the Idura spike, and a line surviving a release that
    /// stopped shipping the package it names. It cannot catch a package that failed to reach
    /// nuget.org - only consuming one from outside can, which tests/consume-nuget does, but on a
    /// weekly schedule and after a release rather than on a pull request. So nothing before a
    /// merge proves a package resolves.
    /// </remarks>
    [Fact]
    public void Every_package_the_documentation_tells_a_reader_to_install_is_one_we_publish()
    {
        var install = new Regex(@"dotnet add package (StubId\.[A-Za-z.]+)");
        List<string> wrong = [];
        var found = 0;

        foreach (var (relative, full) in Scanned())
        {
            if (!relative.StartsWith("docs/", StringComparison.Ordinal) && relative != "README.md")
            {
                continue;
            }

            foreach (Match match in install.Matches(File.ReadAllText(full)))
            {
                found++;
                var id = match.Groups[1].Value;
                var project = Path.Combine(Repository.Root, "src", id, $"{id}.csproj");

                if (!File.Exists(project))
                {
                    wrong.Add($"{relative}: {id} is not a project under src/");
                }
                else if (File.ReadAllText(project).Contains("<IsPackable>false</IsPackable>",
                             StringComparison.Ordinal))
                {
                    wrong.Add($"{relative}: {id} is not published");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

        // A regex that quietly stopped matching would pass this test by finding nothing.
        Assert.True(found > 0, "no install instruction was found in the documentation at all");
    }

    /// <summary>
    /// A package is advertised on nuget.org exactly when a guide says to install it.
    /// </summary>
    /// <remarks>
    /// <c>PackageTags</c> is what makes a package findable by searching, and
    /// <c>StubId.Profiles.Abstractions</c> writes the rule down for itself, StubId.Abstractions
    /// and StubId.Wire: they ship only because packages a reader installs depend on them, and
    /// "a dependency a reader finds by searching the tags is a dependency they will reference
    /// directly". The rule was true of those three and never of StubId.Server, which carried
    /// tags and a readme from before the first release while every document called it substrate -
    /// so <c>docs/releases/2026.09.1.md</c> shipped a sentence that was false about a quarter of
    /// what it described.
    /// <para>
    /// Both halves are read rather than listed. The advertised set comes from the project files
    /// and the installable set from the <c>dotnet add package</c> lines in the guides, so a
    /// package that starts being recommended, or stops, moves both sides together or fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void Exactly_the_packages_a_guide_says_to_install_are_advertised()
    {
        var advertised = Directory
            .EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.csproj",
                SearchOption.AllDirectories)
            .Where(project => File.ReadAllText(project)
                .Contains("<PackageTags>", StringComparison.Ordinal))
            .Select(project => Path.GetFileNameWithoutExtension(project)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var installable = Scanned()
            .Where(file => file.Relative.StartsWith("docs/guides/", StringComparison.Ordinal))
            .SelectMany(file => new Regex(@"dotnet add package (StubId\.[A-Za-z.]+)")
                .Matches(File.ReadAllText(file.Full))
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(installable);
        Assert.Equal(installable, advertised);
    }

    /// <summary>
    /// What a reader could copy from, which is what has to be right.
    /// </summary>
    /// <remarks>
    /// Release notes are exempt because naming the version they shipped is the point of them,
    /// and the release workflow is exempt because it builds its tag set from the property
    /// rather than writing one down.
    /// </remarks>
    private static IEnumerable<(string Relative, string Full)> Scanned()
    {
        foreach (var full in Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(full) is not (".cs" or ".md" or ".yml" or ".yaml" or ".json"
                or ".sh" or ".props" or ".slnx"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(Repository.Root, full).Replace('\\', '/');
            var segments = relative.Split('/');

            if (segments.Any(s => s is ".git" or "bin" or "obj" or "node_modules" or "target" or "_site")
                || relative.StartsWith("docs/releases/", StringComparison.Ordinal)
                || relative == ".github/workflows/release.yml"
                || segments[^1] == "capture.local.json"
                || (relative.StartsWith("docs/api/", StringComparison.Ordinal)
                    && Path.GetExtension(full) == ".yml"))
            {
                continue;
            }

            yield return (relative, full);
        }
    }
}
