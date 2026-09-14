using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>Locates the working tree, so tests can read the committed fixtures.</summary>
public static class Repository
{
    public static string Root { get; } = Find();

    public static string Fixtures => Path.Combine(Root, "fixtures");

    /// <summary>
    /// Every pack in the tree, as a path under <see cref="Fixtures" />: <c>neb/pp</c>.
    /// </summary>
    /// <remarks>
    /// Found by the <c>MANIFEST.json</c> that makes a directory a pack rather than listed, so a
    /// pack recorded later is covered the day it arrives. Walks <c>fixtures/</c> rather than the
    /// whole tree, which keeps it clear of any build output a manifest might ever be copied into.
    /// </remarks>
    public static IReadOnlyList<string> Packs { get; } =
    [
        .. Directory.EnumerateFiles(Fixtures, "MANIFEST.json", SearchOption.AllDirectories)
            .Select(manifest => Path.GetRelativePath(Fixtures, Path.GetDirectoryName(manifest)!)
                .Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    public static string NebPreProduction => Path.Combine(Fixtures, "neb", "pp");

    /// <summary>A file in the first broker's unattended pack, which most tests read.</summary>
    public static string Fixture(string captureId, string file) =>
        Fixture(BrokerTarget.NetsEidBroker, captureId, file);

    /// <summary>A file in a broker's unattended pack, where the target says which.</summary>
    public static string Fixture(BrokerTarget target, string captureId, string file) =>
        Path.Combine(Root, target.Pack, captureId, file);

    /// <summary>The sitting's pack, whose cases are directories of exchanges rather than files.</summary>
    public static string NebSession => Path.Combine(Fixtures, "neb", "pp-session");

    public static string SessionFixture(string captureId, string exchange, string file) =>
        Path.Combine(NebSession, captureId, exchange, file);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
