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
    /// <summary>What a section found.</summary>
    /// <remarks>
    /// A problem is a refusal and a warning is something to read, and the two used to be counted
    /// in one integer. Four lines printed the word PROBLEM and then incremented the warning
    /// count, so a key file that was not there, an issuer belonging to somebody else and a broker
    /// that does not take the algorithm the harness signs with all exited zero.
    /// </remarks>
    private readonly record struct Tally(int Problems, int Warnings)
    {
        public static Tally None => new(0, 0);

        public static Tally Problem => new(1, 0);

        public static Tally Warning => new(0, 1);

        public static Tally operator +(Tally left, Tally right) =>
            new(left.Problems + right.Problems, left.Warnings + right.Warnings);
    }

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

        // The boolean, not the string it hands back. A failed resolution returns the template
        // it could not resolve, which is not empty - so testing the string reported everything
        // below as reachable and then threw on the first fetch, which is the crash this whole
        // section exists to avoid.
        var live = target.TryResolveAuthority(out var authority);
        if (live)
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
        var credentials = Scrubber.Credentials.Where(c => c.Broker == target.Broker).ToList();

        // Measured rather than guessed. The column was a literal 34, one wider than the longest
        // name there was, and a broker with four registrations brought names of 37 - so the
        // column stopped being a column. This keeps the first broker's report identical: its
        // longest name is 33, and 33 plus the two spaces below is the 34 plus one that was there.
        var width = credentials.Count == 0 ? 0 : credentials.Max(c => c.Setting.Length);

        foreach (var (_, _, name) in credentials)
        {
            var value = LocalSettings.Get(name);

            if (value is null)
            {
                unset++;
                Console.WriteLine($"  {name.PadRight(width)}  missing");
                warnings += ReportCost(name, target);
                continue;
            }

            Console.WriteLine($"  {name.PadRight(width)}  set, {value.Length} characters");
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
        Console.WriteLine("Request objects");

        // The key first, because reading it needs nothing but the file. What the broker
        // advertises needs the broker, and a key that does not resolve is worth saying either way.
        var key = ReportKeyMaterial(target);
        problems += key.Problems;
        warnings += key.Warnings;

        if (live)
        {
            var objects = await ReportRequestObjectsAsync(target, ct);
            problems += objects.Problems;
            warnings += objects.Warnings;
        }
        else
        {
            warnings += Skipped();
        }

        Console.WriteLine();
        Console.WriteLine("Signing keys");
        warnings += live ? await ReportKeysAsync(target, ct) : Skipped();

        Console.WriteLine();

        // Whether anything can be recorded is a different question from whether the configuration
        // is complete, and this used to answer the second while appearing to answer the first. A
        // broker with no cases is fully configured and still refuses every recording verb, so
        // "Ready to record." sent the reader off to run one and get an exit code 2.
        var cases = CaptureCatalog.For(target.Broker).Count + ManualCatalog.For(target.Broker).Count;

        if (problems > 0 || warnings > 0)
        {
            Console.WriteLine($"{problems} problem(s), {warnings} warning(s).");
        }
        else if (cases == 0)
        {
            Console.WriteLine($"Configured. No case names {target.Display} yet, so capture, verify,");
            Console.WriteLine("rehearse and session all refuse; nothing above is wrong.");
        }
        else
        {
            Console.WriteLine("Ready to record.");
        }

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
    /// <summary>
    /// Whether this broker takes the request objects the harness would send it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One fetch, and it answers the question a sitting would otherwise answer the expensive way.
    /// The harness signed HS256 because the first broker accepts it; the second advertises nine
    /// algorithms and not one of them is symmetric, and nothing would have said so until a step
    /// that needed a signed request came back refused by a page that declines to explain why.
    /// </para>
    /// <para>
    /// The issuer comparison is here for a smaller reason and a commoner mistake: on a broker
    /// whose tenant is its hostname, a wrong subdomain in the settings produces a document that
    /// parses perfectly and belongs to somebody else.
    /// </para>
    /// </remarks>
    private static async Task<Tally> ReportRequestObjectsAsync(BrokerTarget target, CancellationToken ct)
    {
        string document;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            document = await client.GetStringAsync(target.DiscoveryUrl, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  WARNING  the discovery document could not be fetched: {error.Message}");
            return Tally.Warning;
        }

        var tally = Tally.None;

        using var parsed = JsonDocument.Parse(document);
        var root = parsed.RootElement;

        var issuer = root.TryGetProperty("issuer", out var value) ? value.GetString() : null;
        if (issuer == target.Authority)
        {
            Console.WriteLine($"  ok       the issuer is the authority this is configured with");
        }
        else
        {
            Console.WriteLine("  PROBLEM  the issuer is not the authority this is configured with.");
            Console.WriteLine($"           configured {target.Authority}");
            Console.WriteLine($"           serves     {issuer ?? "(no issuer member)"}");
            tally += Tally.Problem;
        }

        var advertised = root.TryGetProperty("request_object_signing_alg_values_supported", out var algorithms)
            && algorithms.ValueKind == JsonValueKind.Array
                ? algorithms.EnumerateArray().Select(a => a.GetString()).ToList()
                : [];

        if (advertised.Count == 0)
        {
            Console.WriteLine("  WARNING  it advertises no request-object algorithms at all.");
            tally += Tally.Warning;
        }
        else if (advertised.Contains(target.RequestObjectAlgorithm, StringComparer.Ordinal))
        {
            Console.WriteLine($"  ok       {target.RequestObjectAlgorithm} is among the {advertised.Count} it advertises");
        }
        else
        {
            Console.WriteLine($"  PROBLEM  it does not advertise {target.RequestObjectAlgorithm}.");
            Console.WriteLine($"           it takes: {string.Join(" ", advertised)}");
            tally += Tally.Problem;
        }

        return tally;
    }

    /// <summary>
    /// The key a signed step would use, and the public half to register.
    /// </summary>
    /// <remarks>
    /// Registering a key is a copy between two windows, and the mistake it invites - a key
    /// registered that is not the key being signed with - earns a refusal indistinguishable from
    /// a malformed object. Printing the thumbprint on this side makes it a glance rather than an
    /// afternoon.
    /// </remarks>
    private static Tally ReportKeyMaterial(BrokerTarget target)
    {
        if (target.PrivateKeySetting is null)
        {
            Console.WriteLine("  ok       signed with the client secret; there is no key to register");
            return Tally.None;
        }

        if (LocalSettings.Get(target.PrivateKeySetting) is not { Length: > 0 } path)
        {
            Console.WriteLine($"  {target.PrivateKeySetting} is not set, so nothing can be signed.");
            Console.WriteLine("           openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 \\");
            Console.WriteLine("               -out ~/stubid-signicat.pem");
            Console.WriteLine("           The shell expands ~ there. This setting does not, so write");
            Console.WriteLine("           the absolute path into it.");
            return Tally.Warning;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"  PROBLEM  {target.PrivateKeySetting} names a file that is not there.");

            // The likeliest reason, and one the message above reads as a lie: the file is
            // there, and the path was copied from a shell where the shell had expanded it.
            if (path.StartsWith('~'))
            {
                Console.WriteLine("           A leading ~ is not expanded here. Write the absolute path.");
            }

            return Tally.Problem;
        }

        RequestSigner.KeyDescription key;
        try
        {
            key = RequestSigner.Describe(File.ReadAllText(path));
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine($"  PROBLEM  {error.Message}");
            return Tally.Problem;
        }

        Console.WriteLine($"  {key.Algorithm}, {key.Size} bits, sha-256 {key.Thumbprint[..16]}...");

        var keyId = target.KeyIdSetting is null ? null : LocalSettings.Get(target.KeyIdSetting);
        if (keyId is null)
        {
            // A client holding one key may resolve it without being told, but that is a guess.
            Console.WriteLine($"  WARNING  {target.KeyIdSetting} is not set, so the object names no key.");
            Console.WriteLine("           A client holding exactly one may still resolve it.");
        }

        Console.WriteLine();
        Console.WriteLine("  Register this half, and check the thumbprint afterwards:");
        foreach (var line in key.PublicKeyPem.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine($"  {line.TrimEnd()}");
        }

        return keyId is null ? Tally.Warning : Tally.None;
    }

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
    /// <summary>
    /// What a missing value costs, when it costs something nameable.
    /// </summary>
    /// <remarks>
    /// Read from the roster: which registrations name this setting, and which steps use those
    /// registrations. What this replaced asked whether the setting's name contained "SSO_A",
    /// which was a second encoding of the roster written in string matching, and one that could
    /// only ever describe the first broker's naming.
    /// <para>
    /// Where nothing is recorded yet no step is named, so a missing value costs coverage rather
    /// than a recording and the block below says so once.
    /// </para>
    /// </remarks>
    private static int ReportCost(string name, BrokerTarget target)
    {
        var needed = ManualCatalog.StepsNeeding(target, name);
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


    /// <summary>Enough to identify an entry without printing it.</summary>
    private static string Describe(string value) => value.Length <= 4
        ? $"a {value.Length}-character value"
        : $"{value[..2]}...{value[^2..]} ({value.Length} characters)";
}
