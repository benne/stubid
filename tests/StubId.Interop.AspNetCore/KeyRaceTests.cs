using Microsoft.Extensions.Configuration;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Anything sharing a key directory must end up with the same keys.
/// </summary>
/// <remarks>
/// <para>
/// Not hypothetical: it broke the build on Windows first, where file locking is stricter, and
/// it is exactly what two containers sharing a key volume would do. Whoever loses the race
/// keeps the winner's key — a key that differed per caller would defeat the reason for
/// storing one at all.
/// </para>
/// <para>
/// Widened after this failed once in a full suite run and then refused to fail again: not in
/// eight further runs, and not under nineteen hundred concurrent starts. A test that reports
/// only that it failed leaves nothing to work with, so it now runs at five degrees of
/// contention and says which start went wrong and why.
/// </para>
/// </remarks>
public class KeyRaceTests
{
    // Five sizes rather than one repeated, because the analyser rejects duplicates and
    // because the window a race leaves open is not the same width at every degree of
    // contention. The bug this caught showed at eight.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Concurrent_starts_against_one_directory_agree_on_the_keys(int starts)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stubid-race-{Guid.NewGuid():N}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StubId:KeyPath"] = directory })
                .Build();

            var rings = new string[starts];
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, rings.Length, i =>
            {
                try
                {
                    using var keys = new Keys(configuration);
                    rings[i] = string.Join(',', keys.Ring.Keys.Select(k => k.Kid));
                }
                catch (Exception error)
                {
                    // A truncated key file throws here rather than producing a wrong kid, so
                    // the failure has to be reported as itself. Letting Parallel.For wrap it
                    // reports an aggregate and hides which start went wrong.
                    failures.Add(error);
                }
            });

            Assert.Empty(failures.Select(f => f.Message));
            Assert.All(rings, ring => Assert.Equal(rings[0], ring));

            // Nothing left half-written, and nothing deleted out from under another start.
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
            Assert.Equal(3, Directory.EnumerateFiles(directory, "*.pfx").Count());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
