using System.Text.Json;
using System.Text.RegularExpressions;
using StubId.Profiles;
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

    /// <summary>
    /// How this repository writes a link to its own files for a reader who is not in a clone.
    /// </summary>
    /// <remarks>
    /// Stripped before anything is resolved, so a reference written as a full URL is checked as
    /// the path it is. That is not a nicety: the answer an unemulated endpoint gives carries one
    /// of these, the divergences show that answer as an example, and an example nobody checks is
    /// how the documentation went wrong in the first place.
    /// </remarks>
    private const string Blob = "https://github.com/benne/stubid/blob/master/";

    /// <summary>The same for a directory, which is how a citation links to a recording.</summary>
    private const string Tree = "https://github.com/benne/stubid/tree/master/";

    private static IReadOnlyList<FidelityEntry> Ledger() =>
        FidelityLedger.Read(FidelityLedger.Sources);

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
            foreach (var (file, anchor, shown) in References(relative, full))
            {
                var document = Resolve(relative, file);
                if (document is null || !File.Exists(Path.Combine(Repository.Root, document)))
                {
                    dangling.Add($"{relative}  {shown}  (no such document)");
                    continue;
                }

                if (!AnchorsIn(document).Contains(anchor))
                {
                    dangling.Add($"{relative}  {shown}");
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
            .SelectMany(file => References(file.Relative, file.Full)
                .Select(r => (Document: Resolve(file.Relative, r.File), r.Anchor)))
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
    /// Every recording the documentation cites by name is one that is still there, and says whose.
    /// </summary>
    /// <remarks>
    /// The reference cites captures in prose - "Recorded in CAP-023", "CAP-031 settled the other
    /// half" - and prose is exactly where nothing was looking. The ledger's evidence paths are
    /// checked; these were not, and there are more of them.
    /// <para>
    /// The runbook may name a bare id nothing holds, and it is the one document that has to. It
    /// assigns capture numbers before anything is recorded under them - a range for the next
    /// sitting, a starting number for the next unattended batch - so naming one that does not exist
    /// is what it is for. A path or a link it writes still points at a recording, and is checked.
    /// Every other document cites a recording as evidence, and evidence has to be there.
    /// </para>
    /// <para>
    /// Where it has to be depends on whose recording it is, because the numbering restarts per
    /// broker - see <see cref="Unresolved" />. The packs themselves are found rather than listed, so
    /// one recorded later is resolved against the day it arrives.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_capture_the_documentation_cites_is_one_that_exists()
    {
        var packs = Packs();
        var documents = Markdown(Docs).Select(f => (f.Relative, Text: File.ReadAllText(f.Full))).ToList();

        var missing = documents
            .SelectMany(d => Unresolved(d.Relative, d.Text, packs, path =>
                Directory.Exists(Path.Combine(Repository.Root, path))
                || File.Exists(Path.Combine(Repository.Root, path))))
            .ToList();

        Assert.True(missing.Count == 0,
            $"These cite a recording that is not in the tree, or do not say whose it is:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));

        Assert.True(
            documents.Any(d => CaptureIds(d.Text).Any()),
            "No capture citation was found at all, so this test is checking nothing.");

        // By the rule PacksFor scopes with, not a looser one beside it. Checked by prefix, a page
        // directly under docs/brokers/ satisfied this while its citations skipped scoping entirely,
        // so the one assertion meant to prove the scoped branch runs could pass while it never did.
        Assert.True(
            documents.Any(d => BrokerOf(d.Relative) is not null && CaptureIds(d.Text).Any()),
            "No broker page cites a capture, so the rule scoping a citation to its broker checks nothing.");

        Assert.True(
            documents.Any(d => BrokerOf(d.Relative) is null && d.Relative != Runbook
                && Regex.IsMatch(Prose(d.Text), @"fixtures/[a-z0-9-]+/[a-z0-9-]+/CAP-\d{3}")),
            "No page under no broker cites a capture by its pack, so the rule that makes one say whose "
            + "it is checks nothing.");
    }

    private const string Runbook = "docs/capture-session.md";

    /// <summary>
    /// What one page cites that is not in the tree, or cites without saying whose it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capture id alone names a recording only on a page that belongs to a broker, and there it
    /// resolves against that broker's packs - see <see cref="PacksFor" />. Anywhere else a citation
    /// names its pack, as a link to the recording or as the recording's path, and a bare id is
    /// reported. Tried against every pack instead, a release note citing the second broker's
    /// unrecorded sitting passed against the first broker's recordings of the same numbers.
    /// </para>
    /// <para>
    /// A path is resolved as the path it is, and so is a link's target. Matched by its id alone,
    /// <c>fixtures/neb/pp/CAP-020</c> passed because a different pack had a CAP-020.
    /// </para>
    /// <para>
    /// A link to the runbook makes the ids in its text a mention - a range it plans, say - and whether
    /// its anchor is there is for the anchor check to say. Any other link is read like the prose
    /// around it. A fenced code block is not prose: an id in one is a value on the wire.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Unresolved(
        string document, string text, IReadOnlyList<string> packs, Func<string, bool> exists)
    {
        var problems = new List<string>();
        var source = Prose(text);

        // A reference link's target is written once, below the paragraph. The label is blanked so it
        // does not read as a link itself, and the target is left for the path rule further down. A
        // line counts only at the start of the page, after a blank line or after another definition,
        // so it cannot interrupt a paragraph. That is stricter than CommonMark, which also takes one
        // straight after a heading; there the label reads as a bare id and is reported.
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prose = Regex.Replace(
            source,
            @"^(?<label> {0,3}\[(?<name>[^\[\]]+)\]:)[ \t]*<?(?<target>[^\s<>]+)>?"
            + @"(?:[ \t]+(?:""[^""\n]*""|'[^'\n]*'|\([^()\n]*\)))?[ \t]*\r?$",
            definition =>
            {
                if (!StartsABlock(source, definition.Index))
                {
                    return definition.Value;
                }

                definitions.TryAdd(definition.Groups["name"].Value, definition.Groups["target"].Value);
                return new string(' ', definition.Groups["label"].Length)
                    + definition.Value[definition.Groups["label"].Length..];
            },
            RegexOptions.Multiline);

        prose = Regex.Replace(
            prose,
            @"\[(?<text>[^\[\]]*)\](?:\((?<inline>[^)\s]*)\)|\[(?<label>[^\[\]]*)\])?",
            link =>
            {
                var label = link.Groups["label"] is { Success: true, Length: > 0 } named
                    ? named.Value
                    : link.Groups["text"].Value;

                if ((link.Groups["inline"].Success ? link.Groups["inline"].Value : definitions.GetValueOrDefault(label))
                    is not { } target)
                {
                    return link.Value;
                }

                var path = target.Split('#')[0].TrimEnd('/');
                var ids = CaptureIds(link.Groups["text"].Value).ToList();

                if (path.StartsWith("fixtures/", StringComparison.Ordinal))
                {
                    if (!exists(path))
                    {
                        problems.Add($"{document}  {path}  (no such recording)");
                    }
                    else
                    {
                        problems.AddRange(ids
                            .Where(id => !exists(path.Split('/').Contains(id) ? path : $"{path}/{id}"))
                            .Select(id => $"{document}  {id}  (not at {path})"));
                    }

                    return new string(' ', link.Length);
                }

                if (path.Contains("fixtures/", StringComparison.Ordinal) && (ids.Count > 0 || CaptureIds(path).Any()))
                {
                    problems.Add($"{document}  {link.Value}  (a link to a recording that is not this repository's master)");
                    return new string(' ', link.Length);
                }

                return path.Length > 0
                    && !path.Contains("://", StringComparison.Ordinal)
                    && Resolve(document, path) == Runbook
                        ? new string(' ', link.Length)
                        : link.Value;
            });

        prose = Regex.Replace(
            prose,
            @"fixtures/[a-z0-9-]+/[a-z0-9-]+/CAP-[A-Za-z0-9_]+(?:/[A-Za-z0-9_./-]*[A-Za-z0-9_-])?",
            path =>
            {
                if (!exists(path.Value))
                {
                    problems.Add($"{document}  {path.Value}  (no such recording)");
                }

                return new string(' ', path.Length);
            });

        if (document != Runbook)
        {
            problems.AddRange(CaptureIds(prose).Distinct(StringComparer.Ordinal)
                .Where(id => BrokerOf(document) is null
                    || !PacksFor(document, packs).Any(pack => exists($"{pack}/{id}")))
                .Select(id => BrokerOf(document) is null
                    ? $"{document}  {id}  (names no pack, on a page under no broker)"
                    : $"{document}  {id}"));
        }

        return problems.Distinct(StringComparer.Ordinal);
    }

    /// <summary>Whether a line at this index can begin a block, rather than continue a paragraph.</summary>
    private static bool StartsABlock(string text, int index)
    {
        var before = text[..index];
        before = before.EndsWith('\n') ? before[..^1] : before;
        before = before.EndsWith('\r') ? before[..^1] : before;
        var previous = before[(before.LastIndexOf('\n') + 1)..];

        return index == 0
            || previous.Trim().Length == 0
            || Regex.IsMatch(previous, @"^ {0,3}\[[^\[\]]+\]:");
    }

    /// <summary>
    /// A page's text with its fenced code blocks gone and this repository's own URLs reduced to paths.
    /// </summary>
    /// <remarks>A fence closes on a run of its own character at least as long as the one that opened it.</remarks>
    private static string Prose(string text) =>
        Regex.Replace(
                text,
                @"^ {0,3}(?<fence>(?<c>[`~])\k<c>{2,})[^\n]*\n.*?^ {0,3}\k<fence>\k<c>*[ \t]*\r?$",
                "",
                RegexOptions.Multiline | RegexOptions.Singleline)
            .Replace(Blob, "", StringComparison.Ordinal)
            .Replace(Tree, "", StringComparison.Ordinal);

    private static IEnumerable<string> CaptureIds(string text) =>
        Regex.Matches(text, @"\bCAP-\d{3}\b").Select(m => m.Value);

    /// <summary>
    /// The packs a bare capture id on a page resolves against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each broker's unattended pack starts at <c>CAP-001</c> with its own discovery document, so a
    /// capture id means nothing apart from the broker it belongs to. A page under
    /// <c>docs/brokers/&lt;key&gt;/</c> says which broker that is, and resolves against
    /// <c>fixtures/&lt;key&gt;/</c> and nothing else.
    /// </para>
    /// <para>
    /// A page under no broker - the research notes, the roadmap, the release notes - resolves a bare
    /// id against no pack at all, and names the pack in the citation instead.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> PacksFor(string document, IReadOnlyList<string> packs) =>
        BrokerOf(document) is { } broker

            // The trailing slash makes the key a directory rather than a prefix - "ne" must not
            // find "neb".
            ? packs.Where(pack => pack.StartsWith($"fixtures/{broker}/", StringComparison.Ordinal))
            : [];

    /// <summary>The broker a page belongs to, or null for a page under none.</summary>
    /// <remarks>
    /// Four segments at least: a page sitting directly under <c>docs/brokers/</c> belongs to no
    /// broker rather than to one named like its file, and a page nested further in still belongs to
    /// the directory it sits under. The literal <c>brokers</c> is what keeps a note filed in a folder
    /// named after a broker somewhere else in <c>docs/</c> from being read as that broker's reference.
    /// </remarks>
    private static string? BrokerOf(string document) =>
        document.Split('/') is ["docs", "brokers", var broker, _, ..] ? broker : null;

    /// <summary>Every pack in the tree, found by its manifest: <c>fixtures/neb/pp</c>.</summary>
    private static IReadOnlyList<string> Packs() =>
    [
        .. Directory.EnumerateFiles(
                Path.Combine(Repository.Root, "fixtures"), "MANIFEST.json", SearchOption.AllDirectories)
            .Select(manifest => Path.GetRelativePath(Repository.Root, Path.GetDirectoryName(manifest)!)
                .Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// A pack is a broker and an environment, which is what scoping reads the broker from.
    /// </summary>
    /// <remarks>
    /// <see cref="PacksFor" /> takes a pack's second segment as its broker, whatever follows it. A
    /// pack one level too shallow, at <c>fixtures/neb</c>, belongs to no broker and its citations
    /// fail loudly. One nested a level too deep under the wrong broker is the quiet case: a pack at
    /// <c>fixtures/neb/signicat/sandbox</c> would count as the first broker's, and a page about the
    /// first broker citing <c>CAP-001</c> would resolve against the second broker's discovery
    /// document without complaint.
    /// </remarks>
    [Fact]
    public void Every_pack_is_a_broker_and_an_environment()
    {
        var packs = Packs();

        Assert.Contains("fixtures/neb/pp", packs);
        Assert.Contains("fixtures/neb/pp-session", packs);

        var misplaced = packs.Where(pack => pack.Split('/').Length != 3).ToList();
        Assert.True(misplaced.Count == 0,
            "These manifests do not sit at fixtures/<broker>/<env>: " + string.Join(", ", misplaced));
    }

    /// <summary>
    /// The negative control: a broker page does not resolve against another broker's recording.
    /// </summary>
    /// <remarks>
    /// No second broker has a page yet, so the real documentation cannot exercise the
    /// half of the rule that matters. This does.
    /// </remarks>
    [Fact]
    public void A_broker_page_does_not_resolve_against_another_brokers_recording()
    {
        Assert.Empty(PacksFor("docs/brokers/signicat/errors.md", ["fixtures/neb/pp", "fixtures/neb/pp-session"]));
    }

    [Fact]
    public void A_broker_page_resolves_against_its_own_packs_only()
    {
        Assert.Equal(
            ["fixtures/neb/pp", "fixtures/neb/pp-session"],
            PacksFor(
                "docs/brokers/neb/errors.md",
                ["fixtures/neb/pp", "fixtures/neb/pp-session", "fixtures/signicat/sandbox"]));
    }

    /// <summary>"ne" is a prefix of "neb" as text, and names nothing as a directory.</summary>
    [Fact]
    public void A_broker_key_is_matched_as_a_whole_directory()
    {
        Assert.Empty(PacksFor("docs/brokers/ne/errors.md", ["fixtures/neb/pp"]));
    }

    /// <summary>A page nested inside a broker's directory is still that broker's.</summary>
    /// <remarks>
    /// Matched as exactly four segments, a nested page would belong to no broker, and every bare id
    /// deeper in a broker's reference would fail as naming no pack although its directory names one.
    /// When a page under no broker still resolved against every pack, the same slip let such a
    /// citation pass against the other broker's recording, and no test noticed.
    /// </remarks>
    [Fact]
    public void A_nested_broker_page_is_still_scoped_to_its_broker()
    {
        Assert.Equal(
            ["fixtures/signicat/sandbox"],
            PacksFor(
                "docs/brokers/signicat/flows/login.md",
                ["fixtures/neb/pp", "fixtures/signicat/sandbox"]));
    }

    /// <summary>
    /// A page under no broker resolves a bare id against no pack, including one filed in a folder
    /// named like a broker somewhere other than <c>docs/brokers/</c>.
    /// </summary>
    /// <remarks>
    /// The last two rows are what the <c>brokers</c> literal is for. Without it a research note in
    /// <c>docs/research/signicat/</c> would count as that broker's page and cite by bare id, and a
    /// guide in a folder named like one would be scoped to a broker it is not about.
    /// </remarks>
    [Theory]
    [InlineData("docs/research/signed-requests.md")]
    [InlineData("docs/roadmap.md")]
    [InlineData("docs/brokers/index.md")]
    [InlineData("docs/research/signicat/sandbox-notes.md")]
    [InlineData("docs/guides/neb/setup.md")]
    public void A_page_under_no_broker_resolves_a_bare_id_against_no_pack(string document)
    {
        Assert.Empty(PacksFor(document, ["fixtures/neb/pp", "fixtures/signicat/sandbox"]));
    }

    /// <summary>
    /// The negative control for the documentation that exists: a bare id where nothing says whose.
    /// </summary>
    /// <remarks>
    /// The recording is there, which is the point. Before this rule the id resolved, and so did the
    /// same number in the other broker's pack.
    /// </remarks>
    [Fact]
    public void A_bare_id_on_a_page_under_no_broker_says_nothing_about_whose_it_is()
    {
        var problems = Cited("docs/releases/next.md", "Refused, and why (CAP-042).",
            "fixtures/signicat/sandbox/CAP-042");

        Assert.Contains("names no pack", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[CAP-042](https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-042)")]
    [InlineData("[CAP-042](https://github.com/benne/stubid/blob/master/fixtures/signicat/sandbox/CAP-042/)")]
    [InlineData("[CAP-040 to CAP-042](https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox)")]
    [InlineData("[CAP-042]\n\n[CAP-042]: https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-042")]
    [InlineData("[CAP-042][refused]\n\n[refused]: https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-042")]
    [InlineData("`fixtures/signicat/sandbox/CAP-042/response.head`")]
    [InlineData("[the body](https://github.com/benne/stubid/blob/master/fixtures/signicat/sandbox/CAP-042/response.head)")]
    public void A_citation_that_names_its_pack_resolves_there(string citation)
    {
        Assert.Empty(Cited("docs/releases/next.md", citation,
            "fixtures/signicat/sandbox/CAP-040", "fixtures/signicat/sandbox/CAP-042",
            "fixtures/signicat/sandbox/CAP-042/response.head"));
    }

    /// <summary>A citation that names a pack is resolved in that pack, whatever another one holds.</summary>
    /// <remarks>
    /// The second row is the gap the path form had: matched by its id, it passed because the first
    /// broker's session pack has a CAP-020. The third is what holds a range to every id in it, since
    /// the pack directory itself is there. A link whose text names no id is still checked at its
    /// target, and a link to another branch cannot be read at all, so it is reported rather than
    /// passed unread.
    /// </remarks>
    [Theory]
    [InlineData("[CAP-042](https://github.com/benne/stubid/tree/master/fixtures/neb/pp-session/CAP-042)")]
    [InlineData("`fixtures/neb/pp/CAP-020`")]
    [InlineData("[CAP-040 to CAP-043](https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox)")]
    [InlineData("[CAP-042](https://github.com/benne/stubid/tree/main/fixtures/signicat/sandbox/CAP-042)")]
    [InlineData("[why](https://github.com/benne/stubid/tree/main/fixtures/signicat/sandbox/CAP-042)")]
    [InlineData("[why](https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-099)")]
    [InlineData("[the body](https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-042/respons.raw)")]
    [InlineData("`fixtures/signicat/sandbox/CAP-0421`")]
    public void A_citation_that_names_the_wrong_pack_fails(string citation)
    {
        Assert.NotEmpty(Cited("docs/brokers/neb/divergences.md", $"Recorded in {citation}.",
            "fixtures/neb/pp-session/CAP-020", "fixtures/signicat/sandbox/CAP-040",
            "fixtures/signicat/sandbox/CAP-042", "fixtures/neb/pp/CAP-042"));
    }

    [Theory]
    [InlineData("[CAP-020 to CAP-029](../capture-session.md)")]
    [InlineData("[CAP-020 to CAP-029](https://github.com/benne/stubid/blob/master/docs/capture-session.md)")]
    public void A_range_linked_to_the_runbook_that_plans_it_is_a_mention(string range)
    {
        Assert.Empty(Cited("docs/releases/next.md", $"Ten steps, {range}, are declared."));
    }

    /// <summary>Only the runbook turns a citation into a mention. Any other link is read as prose.</summary>
    [Theory]
    [InlineData("docs/releases/next.md", "[CAP-099](#)")]
    [InlineData("docs/releases/next.md", "[CAP-099]()")]
    [InlineData("docs/releases/next.md", "[CAP-099](https://github.com/benne/stubid/pull/75)")]
    [InlineData("docs/releases/next.md", "[CAP-099](../roadmap.md)")]
    [InlineData("docs/brokers/neb/errors.md", "[CAP-099](claims.md)")]
    public void A_link_to_anywhere_but_the_runbook_leaves_its_ids_to_be_resolved(string document, string link)
    {
        Assert.Contains("CAP-099", Assert.Single(Cited(document, $"Recorded in {link}.")), StringComparison.Ordinal);
    }

    /// <summary>A reference link is checked by the ids in its text, at the target its definition gives.</summary>
    [Theory]
    [InlineData("[CAP-043][refused]\n\n[refused]: https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox")]
    [InlineData("[CAP-043]\n\n[CAP-043]: <https://github.com/benne/stubid/tree/master/fixtures/signicat/sandbox/CAP-042> \"the refusal\"")]
    public void A_reference_link_is_resolved_by_its_text(string citation)
    {
        Assert.Contains("CAP-043", Assert.Single(Cited("docs/releases/next.md", citation,
            "fixtures/signicat/sandbox/CAP-042")), StringComparison.Ordinal);
    }

    /// <summary>A line inside a paragraph is not a definition, however much it looks like one.</summary>
    [Fact]
    public void A_line_that_continues_a_paragraph_defines_nothing()
    {
        Assert.Contains("CAP-099", Assert.Single(Cited("docs/releases/next.md",
            "Recorded as\n[CAP-099]: https://github.com/benne/stubid/pull/75")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```\n302 http://localhost:5099/callback?state=CAP-031\n```\n")]
    [InlineData("~~~~ http\nGET /callback?state=CAP-031\n~~~~\n")]
    public void An_id_in_a_fenced_code_block_is_not_a_citation(string block)
    {
        Assert.Empty(Cited("docs/research/signed-requests.md", "It redirects:\n\n" + block + "\nand stops."));
    }

    /// <summary>A code block ends at its own closing fence, and the prose after it is read.</summary>
    /// <remarks>The second row closes with a longer fence than it opened with, which CommonMark allows.</remarks>
    [Theory]
    [InlineData("```\nstate=CAP-031\n```\n")]
    [InlineData("```\nstate=CAP-031\n`````\n")]
    public void A_code_block_ends_at_its_own_fence(string block)
    {
        var problems = Cited("docs/research/signed-requests.md",
            block + "\nRefused, and why (CAP-042).\n\n```\nstate=CAP-031\n```\n");

        Assert.Contains("CAP-042", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_id_on_a_broker_page_resolves_against_that_broker_only()
    {
        Assert.Empty(Cited("docs/brokers/neb/claims.md", "Recorded in CAP-031.", "fixtures/neb/pp-session/CAP-031"));
        Assert.Single(Cited("docs/brokers/signicat/claims.md", "Recorded in CAP-031.", "fixtures/neb/pp-session/CAP-031"));
    }

    [Fact]
    public void The_runbook_may_reserve_a_number_but_not_cite_a_path_that_is_not_there()
    {
        var problems = Cited(Runbook, "Record steps CAP-050 to CAP-059 into `fixtures/signicat/sandbox/CAP-099`.");

        Assert.Contains("fixtures/signicat/sandbox/CAP-099", Assert.Single(problems), StringComparison.Ordinal);
    }

    private static List<string> Cited(string document, string text, params string[] recorded) =>
    [
        .. Unresolved(
            document,
            text,
            ["fixtures/neb/pp", "fixtures/neb/pp-session", "fixtures/signicat/sandbox"],
            path => recorded.Any(r => r == path || r.StartsWith(path + "/", StringComparison.Ordinal))),
    ];

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

    /// <summary>
    /// Every endpoint the discovery document advertises is answered, or says it is not.
    /// </summary>
    /// <remarks>
    /// This is the rule the README states, put where the build can read it. The discovery document
    /// is served from CAP-001 rather than composed, so it advertises everything the broker does -
    /// which is the faithful thing to serve, and leaves StubID naming endpoints it may not
    /// reproduce. One of them it did not: the backchannel authentication endpoint answered 404,
    /// while the README promised a 501 and the divergences said 404 in the same repository.
    /// <para>
    /// Both suffixes count. <c>jwks_uri</c> is an endpoint like the rest of them, and a sweep
    /// matching only <c>_endpoint</c> would have nothing to say about it. The issuer is not: it
    /// names the tenant root rather than a route.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Discoveries))]
    public void Every_endpoint_the_discovery_document_advertises_is_answered_or_declared_missing(
        string broker, string recording)
    {
        var profile = BrokerProfiles.Select(broker);

        using var discovery = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Repository.Root, recording)));

        var issuer = discovery.RootElement.GetProperty("issuer").GetString()!;
        Assert.EndsWith(profile.Root.Prefix, issuer, StringComparison.Ordinal);

        var host = issuer[..^profile.Root.Prefix.Length];

        // Every URL on the broker's own host, rather than the members whose names end a particular
        // way. The second broker advertises a check_session_iframe, which is an address it answers
        // on and ends in neither suffix, so a sweep reading names would have nothing to say about it.
        var advertised = discovery.RootElement.EnumerateObject()
            .Where(m => m.Name != "issuer")
            .Where(m => m.Value.ValueKind == JsonValueKind.String)
            .Where(m => m.Value.GetString()!.StartsWith(host, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(advertised);

        var declared = profile.DeclareRoutes(new ProfileContext(issuer, host))
            .ToDictionary(r => r.Pattern, StringComparer.Ordinal);

        var unanswered = advertised
            .Where(m => !declared.ContainsKey(m.Value.GetString()![host.Length..].TrimStart('/')))
            .Select(m => $"{m.Name} -> {m.Value.GetString()}")
            .ToList();

        Assert.True(unanswered.Count == 0,
            $"{broker}'s discovery advertises these and no route answers them, so they 404 where "
            + $"the README promises a 501:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unanswered));
    }

    /// <summary>The recorded discovery document each broker this build serves is derived from.</summary>
    private static readonly (string Broker, string Recording)[] Recordings =
    [
        ("neb", "fixtures/neb/pp/CAP-001/response.raw"),
    ];

    public static TheoryData<string, string> Discoveries()
    {
        var rows = new TheoryData<string, string>();

        foreach (var (broker, recording) in Recordings)
        {
            rows.Add(broker, recording);
        }

        return rows;
    }

    /// <summary>
    /// Every broker this build serves is covered by the sweep above.
    /// </summary>
    /// <remarks>
    /// The rows are written out, because a broker's discovery recording is not derivable from its
    /// name. This is what stops a second broker being added with no row and the sweep passing by
    /// having nothing to look at.
    /// </remarks>
    [Fact]
    public void Every_broker_this_build_serves_has_a_discovery_recording_to_check()
    {
        Assert.Equal(
            BrokerProfiles.Available.Order(StringComparer.Ordinal),
            Recordings.Select(row => row.Broker).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Anything declared only to admit it is missing says so in the ledger, and says why.
    /// </summary>
    /// <remarks>
    /// The other half of the rule above. A route can satisfy that one by existing, which would let
    /// somebody quietly give CIBA a handler that invents bytes. What stops it is that the
    /// annotation and the answer read the same constant, so a route that stops being unemulated
    /// has to stop claiming to be.
    /// </remarks>
    [Fact]
    public void Nothing_unemulated_is_in_the_ledger_without_a_reason_that_resolves()
    {
        var unemulated = Ledger().Where(e => e.Provenance == "NotEmulated").ToList();

        Assert.NotEmpty(unemulated);

        foreach (var entry in unemulated)
        {
            Assert.True(entry.Complete, $"{entry.Subject} says nothing that can be checked.");
            Assert.Contains("#", entry.Reason!, StringComparison.Ordinal);

            var document = entry.Reason!.Split('#')[0];

            Assert.True(File.Exists(Path.Combine(Repository.Root, document)),
                $"{entry.Subject} points at {document}, which is not there.");

            var anchor = entry.Reason!.Split('#')[1];

            Assert.True(AnchorsIn(document).Contains(anchor),
                $"{entry.Subject} points at #{anchor}, which {document} does not offer.");
        }
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

    /// <summary>Every reference in one file to a section of a document.</summary>
    /// <remarks>
    /// The repository's own full URLs are reduced to the paths they name first, so a link written
    /// either way is checked the same. Anything still carrying a double slash after that belongs
    /// to somebody else's site and is not this build's to verify.
    /// <para>
    /// A document can also point at itself, and those links name no file at all: a bare fragment
    /// rather than one qualified by a file name, which is how a Markdown page normally links
    /// within itself. Reading only the qualified form would call an anchor orphaned while the
    /// paragraph above it linked to the thing.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string File, string Anchor, string Text)> References(
        string relative, string full)
    {
        var text = File.ReadAllText(full).Replace(Blob, "", StringComparison.Ordinal);

        foreach (Match reference in Regex.Matches(
            text, @"(?<file>[A-Za-z0-9_./-]+\.md)#(?<anchor>[a-z0-9-]+)"))
        {
            if (!reference.Value.Contains("//", StringComparison.Ordinal))
            {
                yield return (reference.Groups["file"].Value, reference.Groups["anchor"].Value,
                    reference.Value);
            }
        }

        if (!relative.EndsWith(".md", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (Match reference in Regex.Matches(text, @"\]\(#(?<anchor>[a-z0-9-]+)\)"))
        {
            yield return (Path.GetFileName(relative), reference.Groups["anchor"].Value,
                reference.Value);
        }
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
