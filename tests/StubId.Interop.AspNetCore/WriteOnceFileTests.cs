using System.Text;
using Microsoft.Extensions.Configuration;
using StubId.Server;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// A start that writes a file into the key directory still has what is on disk afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KeyRaceTests" /> starts everything at once and asks whether it agreed, which is the
/// right integration guard and a poor way to find a race: it failed twice in the life of this
/// repository and reproduced neither time. These aim at the window instead. Several starts are
/// held at a barrier so they enter the first-write path together, and one more reads afterwards.
/// </para>
/// <para>
/// That last read is what makes it sharp. The failure is not starts disagreeing while they run -
/// it is one start reading the file it has just written and another replacing that file
/// underneath it. Every racer can be self-consistent while the directory ends up holding content
/// only one of them ever had.
/// </para>
/// </remarks>
public class WriteOnceFileTests
{
    private const int Starts = 8;

    /// <summary>
    /// The invariant itself, run often enough to mean something.
    /// </summary>
    /// <remarks>
    /// The count is measured rather than chosen. Against the <c>File.Move(overwrite: false)</c>
    /// this replaced, about one attempt in a hundred loses the race, so two hundred attempts caught
    /// the regression in seven runs out of eight and a thousand caught it in all six. Racing a
    /// caller instead would mean generating a certificate per attempt, which bought a tenth of the
    /// attempts for ten times the time and still missed.
    /// <see cref="The_certificate_a_start_reads_is_the_one_that_stays_on_disk" /> is what keeps it
    /// honest that the real callers still go through here.
    /// </remarks>
    [Fact]
    public void A_start_that_loses_the_race_does_not_overwrite_the_one_that_won()
    {
        const int attempts = 1000;

        var root = Path.Combine(Path.GetTempPath(), $"stubid-write-once-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            // Eight threads for the whole run rather than eight per attempt. Starting them is what
            // the work costs at this scale - sixteen thousand starts took the better part of a
            // minute and told us nothing the barrier does not.
            var read = new string[attempts, Starts];
            using var together = new Barrier(Starts);

            var threads = Enumerable.Range(0, Starts).Select(i => new Thread(() =>
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    together.SignalAndWait();

                    // Each start would write something different, which is what makes a
                    // replacement visible at all.
                    read[attempt, i] = Encoding.UTF8.GetString(WriteOnceFile.ReadOrCreate(
                        Path.Combine(root, $"{attempt}.bin"),
                        () => Encoding.UTF8.GetBytes($"start-{i}")));
                }
            })).ToList();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var won = read[attempt, 0];

                for (var i = 1; i < Starts; i++)
                {
                    Assert.Equal(won, read[attempt, i]);
                }

                // And it is still what the file holds, which is the half a racer cannot see.
                Assert.Equal(won, File.ReadAllText(Path.Combine(root, $"{attempt}.bin")));
            }

            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                f => f.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// And the TLS certificate really is written that way, not just in principle.
    /// </summary>
    /// <remarks>
    /// Fewer attempts, because each one generates a certificate. This is not where the race is
    /// caught; it is what stops the guard above from passing while a caller quietly stops using
    /// it. That path had no race test of any kind before this.
    /// </remarks>
    [Fact]
    public void The_certificate_a_start_reads_is_the_one_that_stays_on_disk()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"stubid-tls-race-{Guid.NewGuid():N}");

            try
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["StubId:Tls"] = "self-signed",
                        ["StubId:KeyPath"] = directory,
                    })
                    .Build();

                var raced = new string[Starts];
                using var together = new Barrier(Starts);

                var threads = Enumerable.Range(0, Starts).Select(i => new Thread(() =>
                {
                    together.SignalAndWait();
                    raced[i] = Thumbprint(configuration);
                })).ToList();

                foreach (var thread in threads)
                {
                    thread.Start();
                }

                foreach (var thread in threads)
                {
                    thread.Join();
                }

                Assert.All(raced, thumbprint => Assert.Equal(raced[0], thumbprint));
                Assert.Equal(raced[0], Thumbprint(configuration));
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

    private static string Thumbprint(IConfiguration configuration)
    {
        using var certificate = ServerCertificate.Load(configuration);

        Assert.NotNull(certificate);

        return certificate.Certificate.Thumbprint;
    }
}
