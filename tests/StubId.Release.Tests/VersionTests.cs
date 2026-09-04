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
/// compiled constant, a profile identifier and the documentation. Nothing in the language
/// makes those agree, and before this project existed they did not: one image had three tags
/// at once and the profile named a month whose recordings it no longer matched.
/// </remarks>
public class VersionTests
{
    /// <summary>
    /// The version as written, read back off the shipped assembly rather than out of
    /// Directory.Build.props.
    /// </summary>
    /// <remarks>
    /// Reading the assembly proves the evaluated property reached the artefact. Reading the
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

    /// <summary>
    /// The build, the container tag and the recorded broker version are one string.
    /// </summary>
    /// <remarks>
    /// Two different facts that happen to be the same string today. A profile's version is
    /// which recording of the broker is being served; the build's version is which StubID is
    /// being shipped. They coincide because every release so far has carried a sitting.
    /// <para>
    /// The first release that fixes a bug and takes no new recording ends that, and this test
    /// will fail correctly for the wrong reason. When it does: bump the build, leave the
    /// profile alone, and change this test - it becomes a pin on the last recording's date plus
    /// an assertion that the profile is not ahead of the build. Bumping the profile to make it
    /// pass would make the profile version a lie, which is the one thing it cannot afford to
    /// be.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_version_the_assembly_carries_is_the_image_the_module_names()
    {
        var declared = Declared();

        // The tag rather than the whole reference, so this file carries no image literal of its
        // own for the sweep below to find. What the reference should be is pinned where it
        // belongs, by StubIdBuilderTests.
        Assert.Equal(declared, StubIdBuilder.StubIdImage.Split(':')[^1]);
        Assert.Equal(declared, new NetsEidBrokerProfile().Id.Version);
    }

    /// <summary>
    /// The same version in its two published forms, which is why the guides name both.
    /// </summary>
    /// <remarks>
    /// A container tag sorts as text, where 2026.09 precedes 2026.10 and 2026.9 does not, so
    /// the tag keeps its padding. NuGet reads a version as numbers and normalises the zero
    /// away. Neither is wrong; a reader shown only one of them is, which is what makes this a
    /// documented fact rather than an implementation detail. The assembly version is the same
    /// normalisation the package version gets, applied by the SDK where a test can see it.
    /// </remarks>
    [Fact]
    public void The_version_NuGet_publishes_is_the_declared_one_without_its_leading_zeros()
    {
        var normalised = string.Join('.', Declared().Split('.').Select(int.Parse));
        var assembly = typeof(StubIdBuilder).Assembly.GetName().Version!;

        Assert.Equal(normalised, $"{assembly.Major}.{assembly.Minor}.{assembly.Build}");
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

    /// <summary>Every package the documentation tells a reader to install is one we publish.</summary>
    /// <remarks>
    /// The guides named three packages as things a reader uses and gave no way to obtain any of
    /// them, so this is new ground rather than a regression guard. What it catches is a
    /// mistyped identifier, a line naming the Idura spike, and a line surviving a release that
    /// stopped shipping the package it names. It cannot catch a package that failed to reach
    /// nuget.org - only consuming one from outside can, and nothing here does that yet.
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

            if (segments.Any(s => s is ".git" or "bin" or "obj" or "node_modules" or "target")
                || relative.StartsWith("docs/releases/", StringComparison.Ordinal)
                || relative == ".github/workflows/release.yml"
                || segments[^1] == "capture.local.json")
            {
                continue;
            }

            yield return (relative, full);
        }
    }
}
