using System.Text.Json;
using System.Text.RegularExpressions;
using StubId.Server;

namespace StubId.Release.Tests;

/// <summary>
/// The broker reference says what the emulator does, and these check that it still does it.
/// </summary>
/// <remarks>
/// <c>divergences.md</c> opens by claiming that it and the running system "cannot disagree",
/// because both are read from the same annotations. That was asserted rather than enforced: the
/// ledger's own guard checks that a stated reason names a file and throws the anchor away, so a
/// renamed section, a deleted one, or a divergence the ledger never carried all passed.
/// <para>
/// The same gap runs through the rest of the reference. Its tables cite recordings by capture id
/// in prose, where nothing reads them, and <c>claims.md</c> says member order is part of the
/// contract without anything comparing that order to the recording it came from. Prose has been
/// wrong three times already; these are the checks that would have caught it.
/// </para>
/// </remarks>
public class BrokerReferenceTests
{
    private const string Docs = "docs";

    private static IReadOnlyList<FidelityEntry> Ledger() => FidelityLedger.Read(
        typeof(Tokens).Assembly, typeof(StubId.Wire.JwsWriter).Assembly);

    /// <summary>
    /// Every reference to an anchor names one that is actually there.
    /// </summary>
    /// <remarks>
    /// A reason is what an unemulated endpoint sends a caller to, so it has to arrive at the
    /// paragraph that explains the decision rather than at the top of a long document. Both forms
    /// the tree uses are resolved: the explicit anchors the divergences carry, and the slugs
    /// headings get for free.
    /// </remarks>
    [Fact]
    public void Every_anchor_a_reference_names_exists()
    {
        var dangling = new List<string>();

        foreach (var (relative, full) in Referring())
        {
            foreach (Match reference in Regex.Matches(
                File.ReadAllText(full), @"(?<file>[A-Za-z0-9_./-]+\.md)#(?<anchor>[a-z0-9-]+)"))
            {
                var document = Resolve(relative, reference.Groups["file"].Value);
                if (document is null || !File.Exists(Path.Combine(Repository.Root, document)))
                {
                    dangling.Add($"{relative}  {reference.Value}  (no such document)");
                    continue;
                }

                if (!AnchorsIn(document).Contains(reference.Groups["anchor"].Value))
                {
                    dangling.Add($"{relative}  {reference.Value}");
                }
            }
        }

        Assert.True(dangling.Count == 0,
            $"These name an anchor that is not there:{Environment.NewLine}"
            + string.Join(Environment.NewLine, dangling));
    }

    /// <summary>
    /// No section carries an anchor that nothing points at.
    /// </summary>
    /// <remarks>
    /// An explicit anchor is written for a reader who was sent there, so one nobody links to is
    /// either a section that outlived the code it explained or a link that was quietly dropped.
    /// A reference of any kind counts, not only a ledger reason: one of the seven is reached from
    /// a documentation comment rather than from an annotation, and that is a perfectly good
    /// reason to keep it.
    /// </remarks>
    [Fact]
    public void No_anchor_is_left_with_nothing_pointing_at_it()
    {
        var referenced = Referring()
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file.Full), @"(?<file>[A-Za-z0-9_./-]+\.md)#(?<anchor>[a-z0-9-]+)")
                .Select(m => (Document: Resolve(file.Relative, m.Groups["file"].Value), Anchor: m.Groups["anchor"].Value)))
            .Where(r => r.Document is not null)
            .Select(r => $"{r.Document}#{r.Anchor}")
            .ToHashSet(StringComparer.Ordinal);

        var orphaned = Markdown(Docs)
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file.Full), @"<a id=""(?<anchor>[^""]+)""")
                .Select(m => $"{file.Relative}#{m.Groups["anchor"].Value}"))
            .Where(anchor => !referenced.Contains(anchor))
            .ToList();

        Assert.True(orphaned.Count == 0,
            $"Nothing points at these anchors:{Environment.NewLine}"
            + string.Join(Environment.NewLine, orphaned));
    }

    /// <summary>
    /// Every divergence the ledger states has prose a reader can be sent to.
    /// </summary>
    /// <remarks>
    /// The other direction of the same agreement. An entry whose reason resolves to a document
    /// but to no particular place in it has a reason only in the sense that it names a file.
    /// </remarks>
    [Fact]
    public void Every_stated_reason_lands_on_a_section()
    {
        var vague = Ledger()
            .Where(e => e.Reason is not null && e.Reason.Contains('/', StringComparison.Ordinal))
            .Where(e => !e.Reason!.Contains('#', StringComparison.Ordinal))
            .ToList();

        Assert.True(vague.Count == 0,
            "These name a document but no section in it, so a caller sent there has to search: "
            + string.Join(", ", vague.Select(e => $"{e.Subject} -> {e.Reason}")));
    }

    /// <summary>
    /// Every recording the documentation cites by name is one that is still there.
    /// </summary>
    /// <remarks>
    /// The reference cites captures in prose - "Recorded in CAP-023", "CAP-031 settled the other
    /// half" - and prose is exactly where nothing was looking. The ledger's evidence paths are
    /// checked; these were not, and there are more of them.
    /// <para>
    /// The runbook is exempt, and it is the one document that has to be. It assigns capture
    /// numbers before anything is recorded under them - a range for the next sitting, a starting
    /// number for the next unattended batch - so naming one that does not exist is what it is
    /// for. Every other document cites a recording as evidence, and evidence has to be there.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_capture_the_documentation_cites_is_one_that_exists()
    {
        var packs = new[] { "fixtures/neb/pp", "fixtures/neb/pp-session" };

        var missing = Markdown(Docs)
            .Where(file => file.Relative != "docs/capture-session.md")
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file.Full), @"\bCAP-\d{3}\b")
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !packs.Any(pack =>
                    Directory.Exists(Path.Combine(Repository.Root, pack, id))))
                .Select(id => $"{file.Relative}  {id}"))
            .ToList();

        Assert.True(missing.Count == 0,
            $"These cite a recording that is not in the tree:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));

        Assert.True(
            Markdown(Docs).Any(f => Regex.IsMatch(File.ReadAllText(f.Full), @"\bCAP-\d{3}\b")),
            "No capture citation was found at all, so this test is checking nothing.");
    }

    /// <summary>
    /// The tokens the reference describes carry their members in the order it lists them.
    /// </summary>
    /// <remarks>
    /// <c>claims.md</c> states that member order is part of the contract, which is true and is
    /// why a client library can be strict about it. Until now the tables were typed out by hand
    /// from the recordings and then left alone, so the claim rested on nobody having reordered a
    /// row. Extra rows are allowed and expected: some members are conditional, one slot holds
    /// either of two names, and the transaction-token table is a union of three sittings. What is
    /// checked is that every member a recording actually sends appears, and in that order.
    /// </remarks>
    /// <remarks>
    /// Only the two sections that carry a member table. <c>userinfo token</c> describes its
    /// ordering in prose - that its assurance claims come in a different order from the
    /// id_token's - without listing the members, so there is nothing here to compare. That is a
    /// gap in the reference rather than in this test, and filling it is its own change.
    /// </remarks>
    [Theory]
    [InlineData("id_token", "CAP-024", "id_token.payload.json")]
    [InlineData("transaction token", "CAP-031", "transaction_token.payload.json")]
    public void The_claims_reference_lists_members_in_the_order_the_recording_sends_them(
        string section, string capture, string payload)
    {
        var listed = MembersUnder(section, OrderedTableOnly: true);
        var named = MembersUnder(section, OrderedTableOnly: false);
        var path = Path.Combine(
            Repository.Root, "fixtures", "neb", "pp-session", capture, "token", payload);

        Assert.True(File.Exists(path), $"{path} is not there.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var recorded = document.RootElement.EnumerateObject().Select(m => m.Name).ToList();

        var absent = recorded.Where(m => !named.Contains(m)).ToList();
        Assert.True(absent.Count == 0,
            $"{capture} sends these and the '{section}' section never names them: "
            + string.Join(", ", absent));

        var reordered = new List<string>();
        var furthest = -1;

        foreach (var member in recorded.Where(listed.Contains))
        {
            var at = listed.IndexOf(member);

            if (at < furthest)
            {
                reordered.Add(member);
            }
            else
            {
                furthest = at;
            }
        }

        Assert.True(reordered.Count == 0,
            $"The '{section}' table lists these before members {capture} sends earlier: "
            + string.Join(", ", reordered));
    }

    /// <summary>
    /// A link in the documentation reaches something, and does not leave the documentation to do it.
    /// </summary>
    /// <remarks>
    /// Two failures in one sweep. A link to a file that was moved is the ordinary one. The other
    /// only appears once these pages are a site: three links reached out of <c>docs/</c> into
    /// <c>samples/</c> and <c>.github/</c>, which resolves on a repository page and resolves
    /// nowhere once the directory is published on its own. Those are written as full URLs now,
    /// the way the README already writes every link it carries, for the same reason.
    /// </remarks>
    [Fact]
    public void Every_relative_link_in_the_documentation_stays_inside_it_and_arrives()
    {
        var broken = new List<string>();

        foreach (var (relative, full) in Markdown(Docs))
        {
            foreach (Match link in Regex.Matches(File.ReadAllText(full), @"\]\((?<target>[^)\s]+)\)"))
            {
                var target = link.Groups["target"].Value.Split('#')[0];

                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal))
                {
                    continue;
                }

                var resolved = Resolve(relative, target);

                if (resolved is null || !resolved.StartsWith(Docs + "/", StringComparison.Ordinal))
                {
                    broken.Add($"{relative}  {target}  (leaves {Docs}/)");
                }
                else if (!File.Exists(Path.Combine(Repository.Root, resolved)))
                {
                    broken.Add($"{relative}  {target}  (no such file)");
                }
            }
        }

        Assert.True(broken.Count == 0,
            $"These links do not work, or will not once this is a site:{Environment.NewLine}"
            + string.Join(Environment.NewLine, broken));
    }

    /// <summary>The member names a section carries, in the order it gives them.</summary>
    /// <remarks>
    /// Two readings of one section, because the reference makes two different promises about it.
    /// The ordered table is the one that claims to be in wire order, so it stops at the first
    /// sub-heading: what follows explains particular members rather than continuing the sequence.
    /// A row there can still name more than one member - the hash slot holds either of two, and
    /// the three assurance claims share a row because they always travel together.
    /// <para>
    /// The wider reading takes every name the section mentions anywhere, which is what says a
    /// member is documented at all. The transaction token needs it: six of its members are a row
    /// reading "the transaction text, six members, below" and a sub-table underneath giving both
    /// spellings of each, in two columns. They are documented, in the place the table sends a
    /// reader, and they are not part of the sequence the table above claims.
    /// </para>
    /// </remarks>
    private static List<string> MembersUnder(string section, bool OrderedTableOnly)
    {
        var lines = File.ReadAllLines(
            Path.Combine(Repository.Root, Docs, "brokers", "neb", "claims.md"));

        var start = Array.FindIndex(lines, l => l == $"## {section}");
        Assert.True(start >= 0, $"claims.md has no '## {section}' section.");

        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        var body = lines[start..(end < 0 ? lines.Length : end)];

        if (OrderedTableOnly)
        {
            var subheading = Array.FindIndex(body, l => l.StartsWith("### ", StringComparison.Ordinal));
            body = subheading < 0 ? body : body[..subheading];
        }

        var members = new List<string>();

        foreach (var line in body)
        {
            var cells = OrderedTableOnly
                ? line.StartsWith("| `", StringComparison.Ordinal) ? [line.Split('|')[1]] : []
                : new[] { line };

            members.AddRange(cells
                .SelectMany(cell => Regex.Matches(cell, "`([^`]+)`").Select(m => m.Groups[1].Value)));
        }

        Assert.NotEmpty(members);

        return members;
    }

    /// <summary>Every anchor a document offers: the ones written out, and the ones headings get.</summary>
    private static HashSet<string> AnchorsIn(string relative)
    {
        var text = File.ReadAllText(Path.Combine(Repository.Root, relative));

        var anchors = Regex.Matches(text, @"<a id=""(?<anchor>[^""]+)""")
            .Select(m => m.Groups["anchor"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match heading in Regex.Matches(text, @"^#{1,6} (?<title>.+)$", RegexOptions.Multiline))
        {
            anchors.Add(Slug(heading.Groups["title"].Value));
        }

        return anchors;
    }

    /// <summary>The anchor a heading answers to, the way a Markdown renderer derives it.</summary>
    private static string Slug(string heading)
    {
        var flattened = Regex.Replace(heading.Trim(), @"[`*_]", "");
        var kept = new string([.. flattened.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : c is ' ' or '-' ? '-' : '\0')
            .Where(c => c != '\0')]);

        return kept.Trim('-');
    }

    /// <summary>
    /// Where a reference points, as a path from the root of the tree.
    /// </summary>
    /// <remarks>
    /// Two conventions meet here. An annotation in <c>src/</c> writes the whole path from the
    /// root, because that is what a caller reading the ledger over HTTP can use. A link in a
    /// document writes it relative to itself, because that is what renders. Null means it left
    /// the tree.
    /// </remarks>
    private static string? Resolve(string referrer, string target)
    {
        var from = target.StartsWith(Docs + "/", StringComparison.Ordinal)
            ? target
            : Path.Combine(Path.GetDirectoryName(referrer) ?? "", target);

        var full = Path.GetFullPath(Path.Combine(Repository.Root, from));

        return full.StartsWith(Repository.Root, StringComparison.Ordinal)
            ? Path.GetRelativePath(Repository.Root, full).Replace('\\', '/')
            : null;
    }

    /// <summary>Everything that can carry a reference: the documentation, and the code beside it.</summary>
    private static IEnumerable<(string Relative, string Full)> Referring() =>
        Markdown(Docs).Concat(Sources("src")).Concat(Sources("tests"));

    private static IEnumerable<(string Relative, string Full)> Markdown(string directory) =>
        Under(directory, ".md");

    private static IEnumerable<(string Relative, string Full)> Sources(string directory) =>
        Under(directory, ".cs");

    private static IEnumerable<(string Relative, string Full)> Under(string directory, string extension)
    {
        var root = Path.Combine(Repository.Root, directory);

        foreach (var full in Directory.EnumerateFiles(root, "*" + extension, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Repository.Root, full).Replace('\\', '/');

            if (relative.Split('/').Any(s => s is "bin" or "obj" or "node_modules"))
            {
                continue;
            }

            yield return (relative, full);
        }
    }
}
