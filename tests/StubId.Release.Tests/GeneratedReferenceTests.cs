using System.Text.RegularExpressions;
using StubId.Server;

namespace StubId.Release.Tests;

/// <summary>
/// The tables in the broker reference that are written out from the ledger rather than by hand.
/// </summary>
/// <remarks>
/// Only the tables. The prose around them carries the reasoning - why a divergence was chosen and
/// what it costs somebody - and none of that is derivable from an annotation. What is derivable is
/// the index: which entries exist, how close each one is, on what evidence, and where the reasoning
/// for it is written. That part was typed up beside the code and could drift from it, which is the
/// one failure this whole document exists to prevent.
/// <para>
/// There is no tool to run and remember to run. The test composes the block and compares it to
/// what is committed, so a stale table is a red build; setting <c>STUBID_UPDATE_DOCS=1</c> makes
/// the same test write the file instead. One mechanism, and the fix for a failure is to re-run it.
/// </para>
/// </remarks>
public class GeneratedReferenceTests
{
    /// <summary>The page that argues one broker's divergences, and carries its generated tables.</summary>
    private static string Divergences(string broker) => $"docs/brokers/{broker}/divergences.md";

    /// <summary>Every generated block, for every broker this build serves.</summary>
    /// <remarks>
    /// Read from the profiles rather than listed, so a broker added tomorrow brings its page under
    /// the same check on the day it arrives rather than the day somebody remembers this file.
    /// </remarks>
    public static TheoryData<string, string> Blocks()
    {
        var blocks = new TheoryData<string, string>();

        foreach (var broker in BrokerProfiles.Available)
        {
            blocks.Add(broker, "ledger-index");
            blocks.Add(broker, "awaiting-capture");
        }

        return blocks;
    }

    /// <summary>
    /// What is committed is what the ledger says today.
    /// </summary>
    /// <remarks>
    /// Both sides are newline-normalized, and both halves of that matter. The committed side
    /// because a checkout could carry either ending; the composed side because the obvious way to
    /// build a table writes <c>Environment.NewLine</c>, which differs by platform and produced a
    /// green build on Linux and a red one on Windows with the two texts reading identically in
    /// the failure message.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Blocks))]
    public void The_generated_tables_say_what_the_ledger_says(string broker, string block)
    {
        var path = Path.Combine(Repository.Root, Divergences(broker));
        var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var composed = Replace(committed, block, Compose(broker, block));

        if (Environment.GetEnvironmentVariable("STUBID_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, composed);

            return;
        }

        Assert.True(committed == composed,
            $"The '{block}' table is not what the ledger says. Rewrite it with:"
            + $"{Environment.NewLine}    STUBID_UPDATE_DOCS=1 dotnet test tests/StubId.Release.Tests"
            + $"{Environment.NewLine}{Environment.NewLine}Expected:{Environment.NewLine}"
            + Between(composed, block)
            + $"{Environment.NewLine}Found:{Environment.NewLine}"
            + Between(committed, block));
    }

    /// <summary>
    /// What is composed carries no line ending but the one the documents use.
    /// </summary>
    /// <remarks>
    /// The failure this replaces was invisible on Linux and red on Windows, and its message
    /// showed two texts that read identically. Asserting the property directly means the machine
    /// that builds the table is not also the thing that decides whether the table is right.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Blocks))]
    public void What_is_composed_uses_one_line_ending_on_every_platform(string broker, string block)
    {
        Assert.DoesNotContain('\r', Compose(broker, block));
    }

    /// <summary>Every broker this build serves has a page to argue its divergences on.</summary>
    /// <remarks>
    /// The generated tables are written into it, so a broker without one would have its ledger
    /// composed and compared against nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Brokers))]
    public void Every_broker_this_build_serves_has_a_page(string broker)
    {
        Assert.True(
            File.Exists(Path.Combine(Repository.Root, Divergences(broker))),
            $"{broker} is a broker this build serves, and {Divergences(broker)} is not there.");
    }

    public static TheoryData<string> Brokers() => [.. BrokerProfiles.Available];

    /// <summary>Every block a generator knows how to write is a block the document has.</summary>
    /// <remarks>
    /// A renamed heading would otherwise stop the table being generated without failing anything,
    /// which is the quiet version of the drift this replaces.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Blocks))]
    public void Every_block_this_writes_is_one_the_document_still_has(string broker, string block)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Root, Divergences(broker)));

        Assert.True(
            Regex.Matches(text, $"<!-- generated:begin {block} -->").Count == 1
            && Regex.Matches(text, $"<!-- generated:end {block} -->").Count == 1,
            $"{Divergences(broker)} does not have exactly one '{block}' block.");
    }

    private static string Compose(string broker, string block) => block switch
    {
        "ledger-index" => LedgerIndex(broker),
        "awaiting-capture" => AwaitingCapture(broker),
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No such block."),
    };

    /// <summary>The whole ledger, in the order the admin page puts it in.</summary>
    /// <remarks>
    /// Subjects are written as the ledger names them, which is a type's full name for a type-level
    /// entry and <c>Type.Member</c> for the rest. Normalizing the two would change every subject
    /// in the payload a running instance already serves, so the table shows what the endpoint
    /// shows.
    /// </remarks>
    private static string LedgerIndex(string broker)
    {
        string[] header = ["| What | How close | On what evidence | Because |", "| --- | --- | --- | --- |"];

        return Table(header, FidelityLedger.ReadFor(broker)
            .OrderBy(e => FidelityLedger.ProvenanceWeight(e.Provenance))
            .ThenBy(e => e.Subject, StringComparer.Ordinal)
            .Select(entry =>
                $"| `{entry.Subject}` | {entry.Tier}, {entry.Provenance} "
                + $"| {Cell(entry.Evidence)} | {Because(broker, entry.Reason)} |"));
    }

    /// <summary>What each unsettled entry is waiting for.</summary>
    private static string AwaitingCapture(string broker)
    {
        string[] header = ["| What | What would settle it |", "| --- | --- |"];

        return Table(header, FidelityLedger.ReadFor(broker)
            .Where(e => !string.IsNullOrEmpty(e.AwaitingCapture))
            .OrderBy(e => FidelityLedger.ProvenanceWeight(e.Provenance))
            .ThenBy(e => e.Subject, StringComparer.Ordinal)
            .Select(entry => $"| `{entry.Subject}` | {Cell(entry.AwaitingCapture)} |"));
    }

    /// <summary>
    /// A reason, as a link a reader of this page can follow.
    /// </summary>
    /// <remarks>
    /// The ledger holds a path from the root of the repository, because that is what a caller
    /// reading it over HTTP can resolve. Written into a document that path would be wrong - it
    /// would resolve against the document's own directory - so it becomes a bare fragment when it
    /// points into this file, and a relative path when it points anywhere else.
    /// </remarks>
    private static string Because(string broker, string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return "-";
        }

        var parts = reason.Split('#');
        var anchor = parts.Length > 1 ? "#" + parts[1] : "";

        if (parts[0] == Divergences(broker))
        {
            return $"[why]({(anchor.Length > 0 ? anchor : "#")})";
        }

        var relative = Path.GetRelativePath(Path.GetDirectoryName(Divergences(broker))!, parts[0])
            .Replace('\\', '/');

        return $"[why]({relative}{anchor})";
    }

    /// <summary>
    /// One cell of prose, flattened.
    /// </summary>
    /// <remarks>
    /// An annotation's evidence and the sitting it is waiting for are written as sentences across
    /// several lines of source. A newline inside a cell ends the table, and a pipe starts a new
    /// column, so both have to go.
    /// </remarks>
    private static string Cell(string? value) => string.IsNullOrEmpty(value)
        ? "-"
        : Regex.Replace(value, @"\s+", " ").Replace("|", @"\|", StringComparison.Ordinal).Trim();

    /// <summary>Header rows and body rows as one block, joined with newlines this file chose.</summary>
    /// <remarks>
    /// Not <c>StringBuilder.AppendLine</c>: it writes the platform's line ending, and this text is
    /// compared against a file that has exactly one.
    /// </remarks>
    private static string Table(IEnumerable<string> header, IEnumerable<string> rows) =>
        string.Join('\n', header.Concat(rows));

    private static string Replace(string document, string block, string body) =>
        Regex.Replace(
            document,
            $"(?<begin><!-- generated:begin {block} -->\n).*?(?<end><!-- generated:end {block} -->)",
            m => m.Groups["begin"].Value + body + "\n" + m.Groups["end"].Value,
            RegexOptions.Singleline);

    private static string Between(string document, string block)
    {
        var match = Regex.Match(
            document.Replace("\r\n", "\n", StringComparison.Ordinal),
            $"<!-- generated:begin {block} -->\n(?<body>.*?)<!-- generated:end {block} -->",
            RegexOptions.Singleline);

        return match.Success ? match.Groups["body"].Value : "(no such block)";
    }
}
