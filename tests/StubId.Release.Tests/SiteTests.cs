using System.Text.Json;

namespace StubId.Release.Tests;

/// <summary>
/// The documentation site reaches every page the documentation has.
/// </summary>
/// <remarks>
/// The table of contents is hand-written YAML, which is the one part of the site that can fall
/// behind without anything noticing: a page nobody navigates to still builds, still renders, and
/// is simply never found. Three documents were already in that state before there was a site at
/// all - the broker's errors and request parameters, and one research note - reachable only by
/// somebody who knew the path.
/// </remarks>
public class SiteTests
{
    /// <summary>The mark the site is configured with is a file that is actually there.</summary>
    /// <remarks>
    /// docfx does not check this, and the failure is silent in both directions: a metadata path
    /// pointing at nothing builds clean under <c>--warningsAsErrors</c> and serves a 404, and a
    /// resource entry whose <c>dest</c> lands the file somewhere other than where the page asks
    /// for it does the same. The first wiring of this made the second mistake, writing both files
    /// to <c>images/images/</c> while every page asked for <c>images/</c>. Zero warnings, no icon.
    /// <para>
    /// A path in <c>globalMetadata</c> is relative to the site root, with no leading slash - the
    /// template concatenates it onto the per-page <c>../</c> chain - and content is built from
    /// <c>docs/</c> onto that root, so the file behind it sits at the same path under
    /// <c>docs/</c>. Both live at the root rather than in a subdirectory on purpose: the template
    /// always copies its own <c>favicon.ico</c> and <c>logo.svg</c> there, ours overwrite them,
    /// and a browser probing <c>/favicon.ico</c> without reading the tag then gets ours too.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("_appFaviconPath")]
    [InlineData("_appLogoPath")]
    public void The_site_metadata_names_a_file_that_is_there(string key)
    {
        using var config = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Repository.Root, "docfx.json")));

        var named = config.RootElement
            .GetProperty("build").GetProperty("globalMetadata").GetProperty(key).GetString()!;

        Assert.True(
            File.Exists(Path.Combine(Repository.Root, "docs", named)),
            $"docfx.json sets {key} to \"{named}\", so every page asks the site for that path. "
            + $"Nothing is at docs/{named}, so it resolves to nothing and docfx will not say so.");
    }

    /// <summary>Every page under <c>docs/</c> is somewhere in the navigation.</summary>
    /// <remarks>
    /// The API pages are the exception and are not listed here: docfx writes them from the doc
    /// comments and writes their table of contents with them. What is checked is what a person
    /// wrote.
    /// </remarks>
    [Fact]
    public void Every_document_is_reachable_from_the_table_of_contents()
    {
        var docs = Path.Combine(Repository.Root, "docs");
        var navigation = string.Join(
            '\n',
            Directory.EnumerateFiles(docs, "toc.yml", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        var unreachable = Directory
            .EnumerateFiles(docs, "*.md", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(docs, full).Replace('\\', '/'))
            .Where(page => !page.StartsWith("api/", StringComparison.Ordinal))
            .Where(page => !navigation.Contains($"href: {page}", StringComparison.Ordinal))
            .OrderBy(page => page, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            $"These pages are in docs/ and in no table of contents, so the site builds them and "
            + $"nothing links to them:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unreachable));
    }

    /// <summary>Nothing in the navigation points at a page that is not there.</summary>
    /// <remarks>
    /// The other direction, and the one a rename breaks. docfx reports it too, but only when the
    /// site is built, and this runs in the ordinary suite.
    /// </remarks>
    [Fact]
    public void Every_page_the_table_of_contents_names_exists()
    {
        var docs = Path.Combine(Repository.Root, "docs");
        var missing = new List<string>();

        foreach (var toc in Directory.EnumerateFiles(docs, "toc.yml", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(toc)!;

            foreach (var line in File.ReadAllLines(toc))
            {
                var named = line.Trim();

                if (!named.StartsWith("href: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var page = named["href: ".Length..].Trim();

                if (!page.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(directory, page)))
                {
                    missing.Add(
                        $"{Path.GetRelativePath(Repository.Root, toc).Replace('\\', '/')}  {page}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"The navigation names pages that are not there:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }
}
