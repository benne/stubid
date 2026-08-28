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
    private static IEnumerable<string> AllFiles() =>
        Directory.EnumerateFiles(Repository.Fixtures, "*", SearchOption.AllDirectories);

    public static TheoryData<string> TextFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in AllFiles().Where(f => Path.GetExtension(f) is ".json" or ".head" or ".md" or ".raw"))
        {
            data.Add(Path.GetRelativePath(Repository.Fixtures, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_credential_reaches_the_repository(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Fixtures, relativePath));

        // The broker publishes its open test-client secrets, but a recorded exchange
        // containing something secret-shaped trips every scanner pointed at this repo.
        Assert.DoesNotContain("rnlguc7CM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HrlMPtMS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AfvRfDFt", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_signed_token_reaches_the_repository(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Fixtures, relativePath));

        // Tokens in fixtures are re-signed with the fixture key during scrubbing. An
        // untouched one would carry a real signature over whatever was in it.
        Assert.DoesNotContain("eyJhbGciOi", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_personal_identifier_reaches_the_repository(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Fixtures, relativePath));

        var match = Scrubber.FindCprShapedText(text);

        Assert.False(match.Success,
            $"{relativePath} contains something shaped like a CPR number at offset {match.Index}.");
    }

    [Fact]
    public void Manifest_covers_every_file_and_the_hashes_still_match()
    {
        var manifestPath = Path.Combine(Repository.NebPreProduction, "MANIFEST.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var recorded = manifest.RootElement.GetProperty("files");

        var onDisk = Directory
            .EnumerateFiles(Repository.NebPreProduction, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "MANIFEST.json")
            .Select(f => Path.GetRelativePath(Repository.NebPreProduction, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk.Count, recorded.EnumerateObject().Count());

        foreach (var relative in onDisk)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Repository.NebPreProduction, relative));
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));

            Assert.True(recorded.TryGetProperty(relative, out var expected),
                $"{relative} is not in the manifest.");
            Assert.Equal(expected.GetString(), actual);
        }
    }
}
