namespace StubId.CaptureHarness;

/// <summary>
/// Reprocesses a written session directory with the current rules.
/// </summary>
/// <remarks>
/// A sitting is expensive and its recordings cannot be replayed, so when a rule turns out to
/// be wrong the recordings are repaired rather than recaptured. Idempotent: running it over
/// an already-correct directory changes nothing.
/// </remarks>
public static class Sanitise
{
    public static async Task<int> RunAsync(FixtureStore store, CancellationToken ct)
    {
        if (!Directory.Exists(store.Root))
        {
            Console.Error.WriteLine($"No session at {store.Root}.");
            return 2;
        }

        var repaired = 0;

        foreach (var directory in Directory.EnumerateDirectories(store.Root, "*", SearchOption.AllDirectories))
        {
            var bodyPath = Path.Combine(directory, "response.raw");
            var headPath = Path.Combine(directory, "response.head");
            var name = Path.GetFileName(directory);

            if (File.Exists(bodyPath))
            {
                var body = await File.ReadAllTextAsync(bodyPath, ct);
                var (rewritten, tokens) = TokenFixtures.Extract(body);

                if (tokens.Count > 0)
                {
                    foreach (var (member, token) in tokens)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(directory, $"{member}.header.json"), Scrubber.Scrub(token.Header), ct);
                        await File.WriteAllTextAsync(
                            Path.Combine(directory, $"{member}.payload.json"), Scrubber.Scrub(token.Payload), ct);
                    }

                    await File.WriteAllTextAsync(bodyPath, Scrubber.Scrub(rewritten), ct);
                    Console.WriteLine($"  {Relative(store, bodyPath)}: extracted {string.Join(", ", tokens.Keys)}");
                    repaired++;
                }
            }

            // A callback's headers are the browser's, not the broker's. They are not part of
            // anything StubID reproduces, and the cookie jar among them carried a signed token.
            if (name == "callback" && File.Exists(headPath))
            {
                var lines = await File.ReadAllLinesAsync(headPath, ct);
                var alreadyDone = lines.Length == 2 && lines[1].StartsWith('#');

                if (lines.Length > 1 && !alreadyDone)
                {
                    await File.WriteAllTextAsync(headPath,
                        lines[0] + "\n# Request headers omitted: the browser's, not the broker's.\n", ct);
                    Console.WriteLine($"  {Relative(store, headPath)}: dropped {lines.Length - 1} request headers");
                    repaired++;
                }
            }
        }

        // Repairing a recording does not re-record it. The remarks above promise idempotence,
        // which a fresh date on every run would quietly break.
        await store.WriteManifestKeepingDateAsync(ct);

        Console.WriteLine(repaired == 0 ? "Nothing to repair." : $"Repaired {repaired} file(s); manifest rewritten.");
        return 0;
    }

    private static string Relative(FixtureStore store, string path) =>
        Path.GetRelativePath(store.Root, path).Replace('\\', '/');
}
