using System.Text.Json;

namespace StubId.CaptureHarness;

/// <summary>
/// Checks the local configuration before a sitting.
/// </summary>
/// <remarks>
/// A redaction that does not fire looks exactly like one that was never needed, and the
/// difference only shows up after the recordings are written. This runs the real scrubber
/// over the real configuration and reports what would actually happen. It prints diagnoses,
/// never values.
/// </remarks>
public static class Preflight
{
    public static async Task<int> RunAsync(BrokerTarget target, CancellationToken ct)
    {
        var problems = 0;
        var warnings = 0;

        // Touch a setting first: the file loads lazily, and reading Path before anything
        // triggers it reports "not found" while the rest of the report happily uses it.
        _ = LocalSettings.Get("STUBID_NEB_PP_CLIENT_ID");

        Console.WriteLine($"Broker: {target.Display}");
        Console.WriteLine($"Configuration: {LocalSettings.Path ?? "(no capture.local.json found)"}");
        Console.WriteLine();

        Console.WriteLine("Authority");
        if (target.TryResolveAuthority(out var authority))
        {
            Console.WriteLine($"  {authority}");
        }
        else
        {
            // Not a crash. Reporting what is missing is this command's whole job, so it cannot
            // be a caller that throws on the first thing that is.
            Console.WriteLine($"  cannot resolve {target.AuthorityTemplate}");
            Console.WriteLine("  Everything below that needs the live broker is skipped.");
            warnings++;
        }

        Console.WriteLine();

        // Read from the scrubber rather than from a list of its own. The copy that used to live
        // here is the reason this section could report a clean bill for a credential the scrubber
        // had never been told about: two lists, one of which nobody remembers to edit.
        Console.WriteLine("Credentials");

        var unset = 0;

        foreach (var (_, _, name) in Scrubber.Credentials.Where(c => c.Broker == target.Broker))
        {
            var value = LocalSettings.Get(name);

            if (value is null)
            {
                unset++;
                Console.WriteLine($"  {name,-34} missing");
                warnings += ReportCost(name, target);
                continue;
            }

            Console.WriteLine($"  {name,-34} set, {value.Length} characters");
            warnings += ReportReach(value);
        }

        // Once for the block rather than under every line. Where nothing is recorded yet a
        // missing value costs coverage rather than a step, and saying that four times reads as
        // four problems.
        if (unset > 0 && ManualCatalog.For(target.Broker).Count == 0)
        {
            Console.WriteLine($"           {unset} not set. Nothing records {target.Display} yet, and");
            Console.WriteLine("           the guard that scans committed files can only look for");
            Console.WriteLine("           what is here.");
        }

        Console.WriteLine();
        Console.WriteLine("Redactions");

        var redactions = LocalSettings.Redactions();
        if (redactions.Count == 0)
        {
            Console.WriteLine("  none configured");
        }

        foreach (var (replacement, value) in redactions)
        {
            var description = Describe(value);

            // The replacement must not itself be something the guard would flag, or the
            // scrubbed fixture fails the build for the reason it was scrubbed to avoid.
            if (SensitiveContent.FindCpr(replacement).Found)
            {
                Console.WriteLine($"  PROBLEM  {description}: the replacement is itself CPR-shaped");
                problems++;
                continue;
            }

            // The scrubber replaces exact strings, so a length change desynchronizes a
            // recorded Content-Length from the body it describes.
            var lengthNote = replacement.Length == value.Length
                ? "same length"
                : $"length changes {value.Length} to {replacement.Length}";

            // The real thing: does scrubbing this value actually remove it?
            var sample = $$"""{"claim":"{{value}}"}""";
            var scrubbed = Scrubber.Scrub(sample);

            if (scrubbed.Contains(value, StringComparison.Ordinal))
            {
                Console.WriteLine($"  PROBLEM  {description}: configured but not replaced");
                problems++;
            }
            else
            {
                Console.WriteLine($"  ok       {description}, {lengthNote}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Personal numbers");

        var numbers = redactions.Values.Where(v => SensitiveContent.FindCpr(v).Found).ToList();
        if (numbers.Count == 0)
        {
            Console.WriteLine("  none configured. If the sitting requests the ssn scope, the");
            Console.WriteLine("  identity's number will reach a fixture and fail the build.");
            warnings++;
        }

        foreach (var number in numbers)
        {
            // An exact-string replacement of one form does not cover the other, and the
            // broker may return either.
            var digits = number.Replace("-", "", StringComparison.Ordinal);
            var separated = digits.Length == 10 ? $"{digits[..6]}-{digits[6..]}" : null;
            var counterpart = number.Contains('-', StringComparison.Ordinal) ? digits : separated;

            if (counterpart is null || redactions.Values.Contains(counterpart, StringComparer.Ordinal))
            {
                Console.WriteLine($"  ok       {Describe(number)}: both forms registered");
            }
            else
            {
                Console.WriteLine($"  WARNING  {Describe(number)}: the other form is not registered.");
                Console.WriteLine("           The scrubber matches exact strings, so if the broker");
                Console.WriteLine("           returns the other form it will not be replaced.");
                warnings++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Signing keys");
        warnings += authority is { Length: > 0 }
            ? await ReportKeysAsync(target, ct)
            : Skipped();

        Console.WriteLine();
        Console.WriteLine(problems == 0 && warnings == 0
            ? "Ready to record."
            : $"{problems} problem(s), {warnings} warning(s).");

        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// What the broker publishes today, and whether the committed key set still describes it.
    /// </summary>
    /// <remarks>
    /// A recorded token names the key that signed it by thumbprint, and a thumbprint resolves
    /// to a certificate only while the broker still publishes it. The transaction-signing key
    /// rotated in May 2026, which is how it came to look as though it were distributed outside
    /// the key set. Finding a rotation here costs a minute; finding it after a sitting costs
    /// the sitting's only durable evidence of which key signed what.
    /// </remarks>
    private static int Skipped()
    {
        Console.WriteLine("  skipped: the authority does not resolve.");
        return 0;
    }

    private static async Task<int> ReportKeysAsync(BrokerTarget target, CancellationToken ct)
    {
        string jwks;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            jwks = await client.GetStringAsync(target.JwksUrl, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  WARNING  the key set could not be fetched: {error.Message}");
            return 1;
        }

        var live = Kids(jwks);
        foreach (var kid in live)
        {
            Console.WriteLine($"  {kid}  {TokenFixtures.SubjectFor(kid, jwks) ?? "(no certificate)"}");
        }

        var warnings = 0;

        // Named rather than counted: it is the one key a transaction token is signed by, and
        // its absence is what a sitting recording one needs to know before it starts. A broker
        // that names no such certificate is not checked for one, rather than failed for it.
        if (target.CertificateSubjectMarker is { } marker
            && !live.Any(k => TokenFixtures.SubjectFor(k, jwks)?.Contains(marker, StringComparison.Ordinal) == true))
        {
            Console.WriteLine("  WARNING  no transaction-signing certificate is published.");
            Console.WriteLine("           A transaction token recorded now cannot be bound to a key.");
            warnings++;
        }

        var committed = CommittedKids(target);
        if (committed is not null && !committed.SequenceEqual(live, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"  WARNING  the committed {target.KeySetCaptureId} key set is not what the broker serves.");
            Console.WriteLine(
                $"           That is a rotation rather than a fault, but it makes {target.KeySetCaptureId} stale.");
            warnings++;
        }

        return warnings;
    }

    /// <summary>
    /// The key set this broker's own pack recorded, or null before it has one.
    /// </summary>
    /// <remarks>
    /// Null is the right answer on a broker nothing has recorded yet, and it makes this silent
    /// rather than failed on a first run.
    /// </remarks>
    private static string[]? CommittedKids(BrokerTarget target)
    {
        var path = LocalSettings.Root is null
            ? null
            : Path.Combine(LocalSettings.Root, target.Pack, target.KeySetCaptureId, "response.raw");

        return path is null || !File.Exists(path) ? null : Kids(File.ReadAllText(path));
    }

    private static string[] Kids(string jwksJson)
    {
        try
        {
            using var document = JsonDocument.Parse(jwksJson);
            return [.. document.RootElement.GetProperty("keys").EnumerateArray()
                .Select(k => k.TryGetProperty("kid", out var kid) ? kid.GetString() ?? "" : "")
                .Where(k => k.Length > 0)];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// What a missing value costs, when it costs something nameable.
    /// </summary>
    /// <remarks>
    /// A missing Nets eID Broker credential blocks named steps of a sitting, and the steps can be
    /// listed, so they are. Nothing records Signicat yet, so a missing value there costs coverage
    /// rather than a step - which the block says once rather than four times.
    /// </remarks>
    private static int ReportCost(string name, BrokerTarget target)
    {
        if (ManualCatalog.For(target.Broker).Count == 0)
        {
            return 0;
        }

        var needed = name switch
        {
            "STUBID_NEB_PP_CODE_CLIENT_SECRET" =>
                Steps(target.Broker, c => c.Client is ClientProfile.OpenCode or ClientProfile.OpenImplicit),
            var n when n.Contains("SSO_A", StringComparison.Ordinal) =>
                Steps(target.Broker, c => c.Client is ClientProfile.SsoA or ClientProfile.Restricted),
            var n when n.Contains("SSO_B", StringComparison.Ordinal) =>
                Steps(target.Broker, c => c.Client == ClientProfile.SsoB),
            var n when n.Contains("SSO_C", StringComparison.Ordinal) =>
                Steps(target.Broker, c => c.Client == ClientProfile.Hybrid),
            _ => Steps(target.Broker, c => c.Client == ClientProfile.Private),
        };

        if (needed.Count == 0)
        {
            return 0;
        }

        Console.WriteLine($"           {string.Join(", ", needed)} cannot be recorded without it");
        return 1;
    }

    /// <summary>
    /// Whether a value that is set can also be searched for.
    /// </summary>
    /// <remarks>
    /// It is scrubbed either way - a replace does not care how short the string is. What a short
    /// value cannot survive is being matched as a substring against every file in the repository,
    /// where a short word is in half of them. Saying so is the difference between a guard with a
    /// known blind spot and one with a quiet one.
    /// </remarks>
    private static int ReportReach(string value)
    {
        if (value.Length >= SensitiveContent.ShortestScannableValue)
        {
            return 0;
        }

        Console.WriteLine($"           WARNING  under {SensitiveContent.ShortestScannableValue} characters, so it is replaced in");
        Console.WriteLine("           recordings but not searched for in committed files.");
        return 1;
    }

    private static List<string> Steps(Broker broker, Func<ManualCase, bool> predicate) =>
        [.. ManualCatalog.For(broker).Where(predicate).Select(c => c.Id)];

    /// <summary>Enough to identify an entry without printing it.</summary>
    private static string Describe(string value) => value.Length <= 4
        ? $"a {value.Length}-character value"
        : $"{value[..2]}...{value[^2..]} ({value.Length} characters)";
}
