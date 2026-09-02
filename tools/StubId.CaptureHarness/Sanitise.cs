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

            if (File.Exists(headPath))
            {
                var lines = await File.ReadAllLinesAsync(headPath, ct);
                var alreadyTrimmed = lines.Length == 2 && lines[1].StartsWith('#');
                string[] repairedLines;
                string reason;

                // A callback's headers are the browser's, not the broker's. They are not part
                // of anything StubID reproduces, and the cookie jar among them carried a
                // signed token.
                if (name == "callback" && lines.Length > 1 && !alreadyTrimmed)
                {
                    repairedLines = [lines[0], "# Request headers omitted: the browser's, not the broker's."];
                    reason = $"dropped {lines.Length - 1} request headers";
                }
                else
                {
                    repairedLines = [.. lines.Select(MaskCookieLine)];
                    reason = "masked a cookie value";
                }

                if (!repairedLines.SequenceEqual(lines, StringComparer.Ordinal))
                {
                    await File.WriteAllTextAsync(headPath, string.Join('\n', repairedLines) + "\n", ct);
                    Console.WriteLine($"  {Relative(store, headPath)}: {reason}");
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

    /// <summary>
    /// Takes the value out of an already-written Set-Cookie line and leaves everything else
    /// alone. Idempotent, because masking a masked value produces the same bytes.
    /// </summary>
    private static string MaskCookieLine(string line)
    {
        const string prefix = "Set-Cookie: ";

        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? prefix + FixtureStore.HeaderValue("Set-Cookie", line[prefix.Length..], value => value)
            : line;
    }

    private static string Relative(FixtureStore store, string path) =>
        Path.GetRelativePath(store.Root, path).Replace('\\', '/');
}
