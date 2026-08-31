using Microsoft.Extensions.Configuration;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Two processes sharing a key directory must end up with the same keys.
/// </summary>
/// <remarks>
/// Not hypothetical: it broke the build on Windows first, where file locking is stricter, and
/// it is exactly what two containers sharing a key volume would do. Whoever loses the race
/// keeps the winner's key — a key that differed per process would defeat the reason for
/// storing one at all.
/// </remarks>
public class KeyRaceTests
{
    [Fact]
    public void Concurrent_starts_against_one_directory_agree_on_the_keys()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stubid-race-{Guid.NewGuid():N}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StubId:KeyPath"] = directory })
                .Build();

            var rings = new string[8];

            Parallel.For(0, rings.Length, i =>
            {
                using var keys = new Keys(configuration);
                rings[i] = string.Join(',', keys.Ring.Keys.Select(k => k.Kid));
            });

            Assert.All(rings, ring => Assert.Equal(rings[0], ring));
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
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
