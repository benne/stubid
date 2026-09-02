namespace StubId.Fixtures.Tests;

/// <summary>Locates the working tree, so tests can read the committed fixtures.</summary>
public static class Repository
{
    public static string Root { get; } = Find();

    public static string Fixtures => Path.Combine(Root, "fixtures");

    public static string NebPreProduction => Path.Combine(Fixtures, "neb", "pp");

    public static string Fixture(string captureId, string file) =>
        Path.Combine(NebPreProduction, captureId, file);

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
