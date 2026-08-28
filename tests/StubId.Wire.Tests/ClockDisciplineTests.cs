using System.Text.RegularExpressions;

namespace StubId.Wire.Tests;

/// <summary>
/// Nothing under src/ reads the ambient clock.
/// </summary>
/// <remarks>
/// <para>
/// Token lifetimes are part of the contract, and tests need to move time rather than wait
/// for it: an expiry test that sleeps for five minutes gets deleted, and one that cannot
/// move the clock never gets written. Every timestamp therefore comes from an injected
/// <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// The rule covers all of src/ but lives here because tokens are where it first bites and
/// where breaking it is most expensive. The capture harness under tools/ is exempt: it
/// records when a recording was taken, which is genuinely the wall clock.
/// </para>
/// </remarks>
public class ClockDisciplineTests
{
    public static TheoryData<string> SourceFiles()
    {
        var data = new TheoryData<string>();
        var source = Path.Combine(Repository.Root, "src");

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            data.Add(Path.GetRelativePath(source, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void The_ambient_clock_is_not_read(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Root, "src", relativePath));
        var match = Regex.Match(
            text, @"DateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.False(match.Success,
            $"{relativePath} reads {match.Value}. Take a TimeProvider instead, so a test can "
            + "move time rather than wait for it.");
    }

    [Fact]
    public void The_rule_would_notice_a_violation()
    {
        // A guard nobody has seen fail is not yet a guard.
        var offending = "var now = DateTime.UtcNow;";
        Assert.Matches(@"DateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)", offending);
    }
}
