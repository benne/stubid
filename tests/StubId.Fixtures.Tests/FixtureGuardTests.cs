using System.Security.Cryptography;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// Stops the fixtures from carrying anything they should not.
/// </summary>
/// <remarks>
/// <para>
/// These are not hypothetical. The first capture run wrote a real client secret into a
/// fixture, because the scrubber ran after the form had been percent-encoded and a plain
/// string replace no longer matched. The recorder was fixed; this is what would have caught
/// it either way.
/// </para>
/// <para>
/// In the collection because one of these guards reads what the machine is configured with, and
/// three other classes in this project set those very variables around an action and clear them
/// afterwards. Reading them from a class running beside those three finds whatever the other
/// class had set a moment ago and searches the repository for it — which, the first time this
/// ran, reported twelve files as leaking a client identifier that a test had invented two
/// milliseconds earlier.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
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

                        // The built site and the API pages docfx writes. This guard is about what
                        // reaches the repository, and neither of these does - both are ignored
                        // output. Scanning them checks a template's own files for our mistakes.
                        && !f.Contains($"{Path.DirectorySeparatorChar}_site{Path.DirectorySeparatorChar}")
                        && !(f.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}api{Path.DirectorySeparatorChar}")
                             && Path.GetExtension(f) == ".yml")
                        && Path.GetFileName(f) != "capture.local.json");

    /// <summary>
    /// The extensions worth reading, which is wider than what the harness itself writes.
    /// </summary>
    /// <remarks>
    /// The first three are a recording and the next four are the repository's own text. The rest
    /// are how something reaches the tree beside a recording: a sitting is a person at a browser,
    /// and what comes out of that - an exported HAR, a saved response, a scratch log, a note to
    /// self - are the files nobody thinks to look at afterwards, because none of them was going
    /// to be committed on purpose.
    /// <para>
    /// Not the build output that happens to be text: a vendored script bundle is one long run of
    /// alphanumerics, which is what the token check looks for, and it is somebody else's file.
    /// </para>
    /// </remarks>
    private static readonly string[] Readable =
    [
        ".json", ".head", ".raw", ".md", ".cs", ".yml", ".yaml",
        ".txt", ".log", ".har", ".http", ".csv", ".xml", ".toml", ".env", ".sh", ".py",
    ];

    public static TheoryData<string> TextFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in AllFiles().Where(f =>
                     Readable.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
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
                "samples proving each guard fires: a credential shape, signed tokens, "
                + "personal numbers from Denmark's published test range, and a tenant hostname",
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
    /// Nothing committed names a network that belongs to somebody.
    /// </summary>
    /// <remarks>
    /// An address the broker reports back - <c>transaction_client_ip</c> - describes the machine
    /// a sitting was taken from rather than the broker, and it arrives without anyone writing it
    /// down. Replacing a signed token with a placeholder does not cover it, because a decoded
    /// payload written beside the token for readability carries the same claim. The other guards
    /// look for personal numbers and for credentials, and an address is neither.
    /// <para>
    /// Loopback, the private ranges and the documentation blocks pass, so an example may still
    /// name an address. Everything else fails, including one hidden inside a token.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Files whose dotted quads are object identifiers, and why each one is not an address.
    /// </summary>
    /// <remarks>
    /// Its own list rather than the shared exemption above, which would also stop the personal
    /// number and credential scans from reading these files - they have no reason to be skipped,
    /// and a guard switched off wider than its cause is how coverage is lost quietly.
    /// <para>
    /// An X.509 object identifier with four arcs is textually a dotted quad and there is no rule
    /// that separates them - the authority key identifier is a four-arc identifier and reads
    /// exactly like an address. Narrowing the pattern would cost the guard real addresses, so the
    /// ambiguity is recorded here instead, where it can be read.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> MayContainObjectIdentifiers = new()
    {
        ["tools/StubId.CaptureHarness/Ocsp.cs"] =
            "X.509 object identifiers for the OCSP responses it decodes, one of which - the "
            + "authority key identifier - has four arcs and so reads as an address.",
    };

    /// <summary>Every file excused from the address scan still exists.</summary>
    [Fact]
    public void Every_file_excused_from_the_address_scan_is_still_there()
    {
        Assert.All(MayContainObjectIdentifiers, entry =>
            Assert.True(File.Exists(Path.Combine(Repository.Root, entry.Key)),
                $"{entry.Key} is excused from the address scan but does not exist."));
    }

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_routable_address_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath)
            || MayContainObjectIdentifiers.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        var finding = SensitiveContent.FindRoutableIp(text);

        Assert.False(finding.Found,
            $"{relativePath} names the routable address {finding.Value} ({finding.Location}). "
            + "An address recorded from a live sitting belongs to whoever took it, not to this "
            + "project - redact it and add it to the redact block in capture.local.json.");
    }

    /// <summary>
    /// No private key is written into the tree.
    /// </summary>
    /// <remarks>
    /// The second broker verifies a request object against a key registered on the client, so
    /// the harness now holds a private key. It lives outside the repository and is named by a
    /// setting — but a key pasted in to try something is exactly what gets committed by accident,
    /// and unlike a credential it matches none of the shapes the other guards know.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_private_key_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        var finding = SensitiveContent.FindPrivateKey(text);

        Assert.False(finding.Found,
            $"{relativePath} carries {finding.Value}. A signing key belongs outside the "
            + "repository, named by a setting - rotate it if this reached a commit.");
    }

    /// <summary>
    /// Nothing committed names the account a recording was taken from.
    /// </summary>
    /// <remarks>
    /// The first broker's pre-production host is public and shared, so no recording of it could
    /// say whose it was. Signicat's tenant is the hostname, which puts the account's own subdomain
    /// in the issuer, in every absolute URL inside the discovery document, and in <c>iss</c> in
    /// every token — and, unlike a credential, in the prose of any document written about it.
    /// <para>
    /// This one needs no configuration, which is what separates it from the scan below. The
    /// suffix is the shape, so it holds on a machine that has never recorded anything and on
    /// every machine CI runs on.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(TextFiles))]
    public void No_tenant_host_reaches_the_repository(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        var finding = SensitiveContent.FindTenantHost(text);

        Assert.False(finding.Found,
            $"{relativePath} names the tenant host {finding.Value} ({finding.Location}). "
            + "A tenant subdomain names somebody's account rather than the broker - write "
            + "{{SIGNICAT_DOMAIN}} or <subdomain>, either of which this guard lets through.");
    }

    /// <summary>
    /// The one file whose values are examples by construction.
    /// </summary>
    /// <remarks>
    /// Its own list rather than the shared exemption, for the same reason the address scan keeps
    /// one: excusing it there would switch off five other guards to fix one false report. A
    /// redact block still carrying the example's own values is a real thing to know about, but
    /// the file it would point at is the example rather than the copy.
    /// </remarks>
    private static readonly Dictionary<string, string> MayNameConfiguredValues = new()
    {
        ["capture.local.example.json"] =
            "the file that documents the vocabulary. Every value in it is a placeholder or a "
            + "worked example, and a copy of it left unedited would flag it rather than the copy.",
    };

    /// <summary>Every file excused from the configured-value scan still exists.</summary>
    [Fact]
    public void Every_file_excused_from_the_configured_value_scan_is_still_there()
    {
        Assert.All(MayNameConfiguredValues, entry =>
            Assert.True(File.Exists(Path.Combine(Repository.Root, entry.Key)),
                $"{entry.Key} is excused from the configured-value scan but does not exist."));
    }

    /// <summary>
    /// What this machine can be configured to record with, it can also be configured to find.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every guard above finds things by their shape, which is what lets them run everywhere. A
    /// client identifier, a key identifier and an account word have no shape: they are somebody's
    /// account and look like nothing in particular. So this scan sees exactly as much as the
    /// machine running it is configured to see, and on a fresh checkout it sees nothing.
    /// </para>
    /// <para>
    /// That is not the hole it looks like. The values are only knowable to the people who hold
    /// them, and those are the same people who can commit them. This is the check that used to be
    /// a person grepping every written file by hand after a sitting, which is a step that works
    /// right up until the evening somebody is tired.
    /// </para>
    /// <para>
    /// It reads the whole working tree rather than the index, so an untracked scratch file - a
    /// browser session log, a saved response - is scanned too. Those are the ones nobody thinks
    /// to look at, because they were never going to be committed on purpose.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(TextFiles))]
    public void Nothing_committed_names_a_value_this_machine_is_configured_with(string relativePath)
    {
        if (MayContainSensitiveShapes.ContainsKey(relativePath)
            || MayNameConfiguredValues.ContainsKey(relativePath))
        {
            return;
        }

        var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));

        var finding = SensitiveContent.FindConfigured(text, Configured);

        // Names the setting, never the value. A failure message is read in a terminal, pasted
        // into an issue and kept in a build log, and none of those is a place to put the thing
        // this check exists to keep out of one file.
        Assert.False(finding.Found,
            $"{relativePath} carries the value of {finding.Value} ({finding.Location}). "
            + "It is in the local configuration, which means it identifies this account "
            + "rather than the broker - replace it with the placeholder that stands for it.");
    }

    /// <summary>
    /// Read once. The theory runs per file, and re-reading the configuration for each of them
    /// would parse the same document several hundred times.
    /// </summary>
    private static readonly (string Name, string Value)[] Configured = [.. Scrubber.Configured()];

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
