using System.Security.Cryptography;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// Stops the fixtures from carrying anything they should not.
/// </summary>
/// <remarks>
/// These are not hypothetical. The first capture run wrote a real client secret into a
/// fixture, because the scrubber ran after the form had been percent-encoded and a plain
/// string replace no longer matched. The recorder was fixed; this is what would have caught
/// it either way.
/// </remarks>
public class FixtureGuardTests
{
    /// <summary>
    /// Everything committed, not just the recordings. The first plausible personal number in
    /// this repository arrived in a documentation example, where a guard scoped to fixtures
    /// would never have looked.
    /// </summary>
    private static IEnumerable<string> AllFiles() =>
        Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "capture.local.json");

    public static TheoryData<string> TextFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in AllFiles().Where(f =>
                     Path.GetExtension(f) is ".json" or ".head" or ".md" or ".raw" or ".cs" or ".yml"))
        {
            data.Add(Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_credential_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        // Checks the shape rather than a list of known secrets, so it also catches the ones
        // nobody thought to add to the list. A credential-bearing field should hold a
        // placeholder, or a value the case deliberately made useless.
        var match = Scrubber.FindUnscrubbedCredential(text);

        Assert.False(match.Success,
            $"{relativePath} carries an unscrubbed credential at offset {match.Index}.");
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_signed_token_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        // Found by structure, not by how the header happens to begin: a header starting
        // with typ rather than alg encodes to something the old literal check missed.
        var finding = SensitiveContent.FindSignedToken(text);

        Assert.False(finding.Found,
            $"{relativePath} carries a signed token ({finding.Value}) in {finding.Location}.");
    }

    /// <summary>
    /// The one file that must contain sensitive-looking text: the tests that prove these
    /// rules fire. A guard nobody has seen fail is not a guard, so its samples have to look
    /// like the real thing.
    /// </summary>
    /// <remarks>
    /// Everything else in the repository is scanned, documentation included — which is where
    /// the first plausible personal number here actually appeared, in an example written to
    /// explain the rule.
    /// </remarks>
    private static readonly Dictionary<string, string> MayContainSensitiveShapes =
        new(StringComparer.Ordinal)
        {
            ["tests/StubId.Fixtures.Tests/ScrubberTests.cs"] =
                "samples proving each guard fires: a credential shape, signed tokens, and "
                + "personal numbers from Denmark's published test range",
        };

    [Fact]
    public void Every_exemption_still_names_a_real_file()
    {
        // A stale exemption is a hole nobody remembers opening.
        Assert.All(MayContainSensitiveShapes, entry =>
            Assert.True(File.Exists(Path.Combine(Repository.Root, entry.Key)),
                $"{entry.Key} is exempt from the personal-identifier scan but does not exist."));
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_personal_identifier_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        var finding = SensitiveContent.FindCpr(text);

        Assert.False(finding.Found,
            $"{relativePath} contains something shaped like a CPR number ({finding.Location}).");
    }

    /// <summary>
    /// Both packs. The unattended one is rehashed by every <c>capture</c> run, so it drifts
    /// only briefly; the sitting's manifest is written when somebody finishes a sitting and
    /// not again, and those recordings are the ones no run can reproduce. This test was
    /// covering only the pack that could be recaptured.
    /// </summary>
    [Theory]
    [InlineData("pp")]
    [InlineData("pp-session")]
    public void Manifest_covers_every_file_and_the_hashes_still_match(string pack)
    {
        var root = Path.Combine(Repository.Fixtures, "neb", pack);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "MANIFEST.json")));
        var recorded = manifest.RootElement.GetProperty("files");

        var onDisk = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "MANIFEST.json")
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk.Count, recorded.EnumerateObject().Count());

        foreach (var relative in onDisk)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relative));
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));

            Assert.True(recorded.TryGetProperty(relative, out var expected),
                $"{relative} is not in the {pack} manifest.");
            Assert.Equal(expected.GetString(), actual);
        }
    }
}
