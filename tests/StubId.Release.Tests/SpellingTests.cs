using System.Text.RegularExpressions;

namespace StubId.Release.Tests;

/// <summary>
/// The tree is written in US English.
/// </summary>
/// <remarks>
/// The repository was written in British English until 2026-09-06 and converted in one pass. A
/// conversion nothing enforces is a conversion that lasts until the next contribution, so this
/// sweeps for the British forms rather than trusting the one that already happened. It is the
/// same instinct as the clock and fixture guards: state the rule once, where the build can read
/// it.
/// <para>
/// The word list is the general British set, not the forms that pass happened to remove. A guard
/// built from a diff only ever proves the diff.
/// </para>
/// </remarks>
public class SpellingTests
{
    /// <summary>
    /// Whole words whose British form must not appear. Suffixed families are matched by stem, so
    /// one entry covers <c>-e</c>, <c>-ed</c>, <c>-es</c>, <c>-ing</c> and <c>-ation</c>.
    /// </summary>
    private static readonly (string Pattern, string Use)[] British =
    [
        (@"[A-Za-z]*behaviour[A-Za-z]*", "behavior"),
        (@"[A-Za-z]*artefact[A-Za-z]*", "artifact"),
        (@"[A-Za-z]*organis(e|ed|es|ing|ation)[A-Za-z]*\b", "organization"),
        (@"[A-Za-z]*catalogue[sd]?\b", "catalog"),
        (@"[A-Za-z]*normalis[A-Za-z]*", "normalize"),
        (@"[A-Za-z]*serialis[A-Za-z]*", "serialize"),
        (@"[A-Za-z]*sanitis[A-Za-z]*", "sanitize"),
        (@"[A-Za-z]*authoris[A-Za-z]*", "authorize"),
        (@"[A-Za-z]*recognis[A-Za-z]*", "recognize"),
        (@"[A-Za-z]*initialis[A-Za-z]*", "initialize"),
        (@"[A-Za-z]*materialis[A-Za-z]*", "materialize"),
        (@"[A-Za-z]*containeris[A-Za-z]*", "containerize"),
        (@"[A-Za-z]*generalis[A-Za-z]*", "generalize"),
        (@"[A-Za-z]*specialis[A-Za-z]*", "specialize"),
        (@"[A-Za-z]*summaris[A-Za-z]*", "summarize"),
        (@"[A-Za-z]*customis[A-Za-z]*", "customize"),
        (@"[A-Za-z]*optimis[A-Za-z]*", "optimize"),
        (@"[A-Za-z]*minimis[A-Za-z]*", "minimize"),
        (@"[A-Za-z]*maximis[A-Za-z]*", "maximize"),
        (@"[A-Za-z]*synchronis[A-Za-z]*", "synchronize"),
        (@"[A-Za-z]*prioritis[A-Za-z]*", "prioritize"),
        (@"[A-Za-z]*categoris[A-Za-z]*", "categorize"),
        (@"[A-Za-z]*standardis[A-Za-z]*", "standardize"),
        (@"[A-Za-z]*visualis[A-Za-z]*", "visualize"),
        (@"[A-Za-z]*utilis(e|ed|es|ing|ation)\b", "utilize"),
        (@"[A-Za-z]*apologis[A-Za-z]*", "apologize"),
        (@"[A-Za-z]*globalisation\b", "globalization"),
        (@"[A-Za-z]*idealisation\b", "idealization"),
        (@"[A-Za-z]*analys(e|ed|es|ing|er|ers)\b", "analyze"),
        (@"[A-Za-z]*honour[A-Za-z]*", "honor"),
        (@"[A-Za-z]*neighbour[A-Za-z]*", "neighbor"),
        (@"[A-Za-z]*labour[A-Za-z]*", "labor"),
        (@"[A-Za-z]*favour[A-Za-z]*", "favor"),
        (@"[A-Za-z]*colour[A-Za-z]*", "color"),
        (@"[A-Za-z]*humour[A-Za-z]*", "humor"),
        (@"[A-Za-z]*rumour[A-Za-z]*", "rumor"),
        (@"licence[sd]?\b", "license"),
        (@"defence[s]?\b", "defense"),
        (@"offence[s]?\b", "offense"),
        (@"pretence[s]?\b", "pretense"),
        (@"centre[sd]?\b", "center"),
        (@"metre[s]?\b", "meter"),
        (@"fibre[s]?\b", "fiber"),
        (@"litre[s]?\b", "liter"),
        (@"[A-Za-z]*travell(ed|ing|er|ers)\b", "traveled"),
        (@"[A-Za-z]*cancell(ed|ing)\b", "canceled"),
        (@"[A-Za-z]*modell(ed|ing)\b", "modeled"),
        (@"[A-Za-z]*labell(ed|ing)\b", "labeled"),
        (@"[A-Za-z]*signall(ed|ing)\b", "signaled"),
        (@"[A-Za-z]*fuell(ed|ing)\b", "fueled"),
        (@"judgement[s]?\b", "judgment"),
        (@"acknowledgement[s]?\b", "acknowledgment"),
        (@"programmes?\b", "program"),
        (@"storey[s]?\b", "story"),
        (@"practis(e|ed|es|ing)\b", "practice"),
        (@"dependant[s]?\b", "dependent"),
        (@"orientated\b", "oriented"),
        (@"enquir(e|ed|es|ing|y|ies)\b", "inquire"),
        (@"whilst\b", "while"),
        (@"amongst\b", "among"),
        (@"skilful\b", "skillful"),
        (@"wilful\b", "willful"),
        (@"marvellous\b", "marvelous"),
        (@"instalment[s]?\b", "installment"),
        (@"aluminium\b", "aluminum"),
        (@"manoeuvre[sd]?\b", "maneuver"),
        (@"sulphur[A-Za-z]*", "sulfur"),
        (@"aeroplane[s]?\b", "airplane"),
    ];

    /// <summary>
    /// Words a sweep would otherwise flag, and why each one stays.
    /// </summary>
    /// <remarks>
    /// Matched as whole words, not as substrings: an entry allows the word it names and nothing
    /// that merely contains it.
    /// <para>
    /// The first group is Danish and the second is somebody else's identifier. The third is the
    /// name of a type that existed and was deleted: a page describing what a release removed has
    /// to be able to say what it was called, and the compatibility statement names this one
    /// because it is the counterexample to the deprecation promise rather than an illustration of
    /// it. All three are permanent. The entries that were here for the aliases the US-English
    /// conversion kept are not among them - those went with the aliases in 2026.09.3.
    /// </para>
    /// </remarks>
    private static readonly string[] Allowed =
    [
        "Digitaliseringsstyrelsen",
        "kodeviser",
        "organizationIdentifier",

        "BehaviourApi",
    ];

    /// <summary>
    /// Every British spelling in the tree is one this test names as allowed.
    /// </summary>
    [Fact]
    public void The_tree_is_written_in_US_English()
    {
        var found = new List<string>();

        foreach (var (relative, full) in Scanned())
        {
            var lines = File.ReadAllLines(full);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, use) in British)
                {
                    foreach (Match match in
                        Regex.Matches(lines[i], $@"\b{pattern}", RegexOptions.IgnoreCase))
                    {
                        if (Allowed.Contains(match.Value, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        found.Add($"{relative}:{i + 1}  {match.Value} -> {use}");
                    }
                }
            }
        }

        Assert.True(found.Count == 0,
            $"British spellings found; this repository is written in US English:{Environment.NewLine}"
            + string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// What the sweep reads.
    /// </summary>
    /// <remarks>
    /// Three exemptions, each for a different reason. The recording packs, because their files
    /// are hashed into a manifest and one of those manifests records a sitting that cannot be
    /// repeated - the spelling in a recording is part of the record. A pack is found by the
    /// MANIFEST.json that covers it rather than by the fixtures/ prefix, because not everything
    /// in that directory is a recording: fixtures/README.md and fixtures/neb/certificates.md are
    /// the project's own writing and are held to the same rule as the rest of it. Skipping by
    /// prefix hid them through the conversion. The built site and the API pages docfx writes from
    /// the doc comments, because both are output - the site carries a template's own JavaScript,
    /// and sweeping generated files means sweeping somebody else's spelling; docs/api/index.md is
    /// written by hand and is still read. The release notes, because naming what changed is
    /// the point of them and one of the things that changed was the spelling, so a note about a
    /// rename has to be able to write both. And this file, which cannot sweep for words it is
    /// obliged to contain.
    /// </remarks>
    /// <summary>
    /// The directories a manifest covers, as prefixes. Read from the tree rather than listed here
    /// so a pack recorded later is exempt the day it arrives, and so prose that merely sits near
    /// a pack is not.
    /// </summary>
    private static readonly string[] Packs =
        Directory.EnumerateFiles(Repository.Root, "MANIFEST.json", SearchOption.AllDirectories)
            .Select(manifest =>
                Path.GetRelativePath(Repository.Root, Path.GetDirectoryName(manifest)!)
                    .Replace('\\', '/') + "/")
            .Where(pack => !pack.Split('/').Any(s => s is "bin" or "obj" or "node_modules"))
            .ToArray();

    private static IEnumerable<(string Relative, string Full)> Scanned()
    {
        foreach (var full in Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(full) is not (".cs" or ".md" or ".yml" or ".yaml" or ".json"
                or ".sh" or ".props" or ".slnx" or ".mjs" or ".js" or ".csproj"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(Repository.Root, full).Replace('\\', '/');
            var segments = relative.Split('/');

            if (segments.Any(s => s is ".git" or "bin" or "obj" or "node_modules" or "target" or "_site")
                || Packs.Any(pack => relative.StartsWith(pack, StringComparison.Ordinal))
                || relative.StartsWith("docs/releases/", StringComparison.Ordinal)
                || segments[^1] == "SpellingTests.cs"
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
