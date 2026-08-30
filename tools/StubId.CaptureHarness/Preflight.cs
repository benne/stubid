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
    public static int Run()
    {
        var problems = 0;
        var warnings = 0;

        // Touch a setting first: the file loads lazily, and reading Path before anything
        // triggers it reports "not found" while the rest of the report happily uses it.
        _ = LocalSettings.Get("STUBID_NEB_PP_CLIENT_ID");

        Console.WriteLine($"Configuration: {LocalSettings.Path ?? "(no capture.local.json found)"}");
        Console.WriteLine();

        Console.WriteLine("Credentials");
        foreach (var name in new[]
        {
            "STUBID_NEB_PP_CLIENT_ID",
            "STUBID_NEB_PP_CLIENT_SECRET",
            "STUBID_NEB_PP_CODE_CLIENT_SECRET",
            "STUBID_NEB_PP_SSO_A_CLIENT_ID",
            "STUBID_NEB_PP_SSO_A_CLIENT_SECRET",
            "STUBID_NEB_PP_SSO_B_CLIENT_ID",
            "STUBID_NEB_PP_SSO_B_CLIENT_SECRET",
            "STUBID_NEB_PP_SSO_C_CLIENT_ID",
            "STUBID_NEB_PP_SSO_C_CLIENT_SECRET",
        })
        {
            var value = LocalSettings.Get(name);
            if (value is null)
            {
                var needed = name switch
                {
                    "STUBID_NEB_PP_CODE_CLIENT_SECRET" =>
                        Steps(c => c.Client is ClientProfile.OpenCode or ClientProfile.OpenImplicit),
                    var n when n.Contains("SSO_A", StringComparison.Ordinal) =>
                        Steps(c => c.Client is ClientProfile.SsoA or ClientProfile.Restricted),
                    var n when n.Contains("SSO_B", StringComparison.Ordinal) =>
                        Steps(c => c.Client == ClientProfile.SsoB),
                    var n when n.Contains("SSO_C", StringComparison.Ordinal) =>
                        Steps(c => c.Client == ClientProfile.Hybrid),
                    _ => Steps(c => c.Client == ClientProfile.Private),
                };

                Console.WriteLine($"  {name,-34} missing");
                if (needed.Count > 0)
                {
                    warnings++;
                    Console.WriteLine($"           {string.Join(", ", needed)} cannot be recorded without it");
                }
            }
            else
            {
                Console.WriteLine($"  {name,-34} set, {value.Length} characters");
            }
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

            // The scrubber replaces exact strings, so a length change desynchronises a
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
        Console.WriteLine(problems == 0 && warnings == 0
            ? "Ready to record."
            : $"{problems} problem(s), {warnings} warning(s).");

        return problems == 0 ? 0 : 1;
    }

    private static List<string> Steps(Func<ManualCase, bool> predicate) =>
        [.. ManualCatalogue.All.Where(predicate).Select(c => c.Id)];

    /// <summary>Enough to identify an entry without printing it.</summary>
    private static string Describe(string value) => value.Length <= 4
        ? $"a {value.Length}-character value"
        : $"{value[..2]}...{value[^2..]} ({value.Length} characters)";
}
