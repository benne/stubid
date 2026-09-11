using StubId.CaptureHarness;

// Records the broker exchanges that StubID's fidelity tests assert against.
//
//   capture   record every unattended case and write the fixtures
//             --only=CAP-040,CAP-041 records just those, leaving the rest committed
//   verify    record the unattended cases again and compare against what is committed
//   session   host the relying party for the manual sitting on localhost:5099
//             --only=CAP-031 lists just that step, so the ones already recorded cannot be
//             clicked by accident
//   check     verify the local configuration before a sitting
//   rehearse  send every step's authorize request, without completing any
//             --only applies here too
//   sanitize  reprocess a written session with the current rules
//
// Every verb takes --broker=neb or --broker=signicat, and there is no default. The two write
// into different packs, so a run that guessed would put one broker's recordings where the
// other's belong.
//
// All of it hits a broker's live pre-production or sandbox environment.

// The verb is the first argument that is not a flag, so --broker= may come before or after it.
// Reading args[0] meant `-- --broker=neb capture` ran a verb called "--broker=neb".
var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "capture";

// Recording a case rewrites its bytes, and an error id or a timestamp differs every time. So
// adding a case to the catalog would otherwise churn every fixture beside it, burying the
// one new recording in nineteen diffs that say nothing.
var only = args
    .FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?[7..]
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

BrokerTarget target;
try
{
    target = BrokerTarget.Select(
        args.FirstOrDefault(a => a.StartsWith("--broker=", StringComparison.Ordinal))?[9..]);
}
catch (InvalidOperationException refusal)
{
    Console.Error.WriteLine(refusal.Message);
    return 2;
}

// The same anchor capture.local.json uses, so there is one way to find the tree. The five
// relative steps this replaced were counted from the build output directory, and the session
// pack was then found by walking three of them back and re-appending a literal path - which
// meant any pack at the same depth was written into the first broker's session directory.
if (LocalSettings.Root is not { } repository)
{
    Console.Error.WriteLine(
        "This has to run from a checkout: StubID.slnx is what tells it where the fixtures are.");
    return 2;
}

var root = Path.Combine(repository, target.Pack);
var sessionRoot = Path.Combine(repository, target.SessionPack);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var store = new FixtureStore(root);
using var recorder = new Recorder();
var catalog = CaptureCatalog.For(target.Broker);
var cases = only is null
    ? catalog
    : [.. catalog.Where(c => only.Contains(c.Id, StringComparer.OrdinalIgnoreCase))];

// The two catalogs are filtered separately, because a name in one is not in the other: the
// sitting's steps are deliberately not in CaptureCatalog, and --only=CAP-031 matching
// nothing there is the normal case rather than a mistake.
var manual = ManualCatalog.Selected(target.Broker, only);

switch (command)
{
    case "capture":
        return Refuse(cases) ?? await CaptureAsync();
    case "verify":
        return Refuse(cases) ?? await VerifyAsync();
    case "rehearse":
        return Refuse(manual) ?? await Rehearsal.RunAsync(target, manual, cancellation.Token);
    case "sanitize":
        return await Sanitize.RunAsync(new FixtureStore(sessionRoot), cancellation.Token);
    case "check":
        return await Preflight.RunAsync(target, cancellation.Token);
    case "session":
        // The manual sitting writes into its own directory: the unattended pack must stay
        // reproducible by re-running capture, and these recordings never are.
        return Refuse(manual) ?? await Session.RunAsync(
            target, new FixtureStore(sessionRoot), manual);
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'capture', 'verify', "
            + "'session', 'rehearse', 'sanitize' or 'check'.");
        return 2;
}

int? Refuse<T>(IReadOnlyList<T> selected)
{
    if (selected.Count > 0)
    {
        return null;
    }

    Console.Error.WriteLine(only is null
        ? $"There is nothing to record for {target.Display} yet."
        : "No case matched --only.");
    return 2;
}

async Task<int> CaptureAsync()
{
    Console.WriteLine($"Recording {cases.Count} {target.Display} cases into {root}");
    var surprises = new List<string>();

    foreach (var @case in cases)
    {
        RecordedExchange exchange;
        try
        {
            exchange = await recorder.RecordAsync(@case, cancellation.Token);
        }
        catch (InvalidOperationException error)
        {
            // A missing credential is a setup problem, not a crash. Say what to do and stop
            // before the manifest is rewritten from a half-finished run.
            Console.Error.WriteLine($"  {@case.Id}  cannot record: {error.Message}");
            return 2;
        }

        await store.WriteAsync(@case, exchange, cancellation.Token);

        var actual = DispositionClassifier.Classify(exchange);
        if (actual != @case.Expected)
        {
            surprises.Add($"{@case.Id}: expected {@case.Expected}, got {actual}");
        }

        Console.WriteLine(
            $"  {@case.Id}  {exchange.StatusCode} {actual,-14} {@case.Description}"
            + (actual == @case.Expected ? "" : $"  <-- expected {@case.Expected}"));

        // The catalog says which case is the key set, rather than this spelling a case id the
        // catalog is free to change underneath it.
        if (@case.Id == target.KeySetCaptureId)
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, target.CertificateReportPath),
                CertificateReport.Build(exchange.ResponseBody),
                cancellation.Token);
        }
    }

    // A partial run must not restamp the pack: the date says when these recordings were
    // made, and most of them were not made today.
    await (only is null
        ? store.WriteManifestAsync(FixtureStore.Now(), cancellation.Token)
        : store.WriteManifestKeepingDateAsync(cancellation.Token));

    Console.WriteLine("Wrote MANIFEST.json");

    if (surprises.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("The broker answered differently than the catalog expects:");
        foreach (var line in surprises)
        {
            Console.Error.WriteLine($"  {line}");
        }

        return 1;
    }

    return 0;
}

async Task<int> VerifyAsync()
{
    Console.WriteLine(
        $"Re-recording {cases.Count} {target.Display} cases and comparing against {root}");
    var drifted = new List<string>();

    foreach (var @case in cases)
    {
        var directory = store.DirectoryFor(@case);
        if (!Directory.Exists(directory))
        {
            drifted.Add($"{@case.Id}: no committed fixture");
            continue;
        }

        var fresh = await recorder.RecordAsync(@case, cancellation.Token);

        var committedBody = await File.ReadAllBytesAsync(
            Path.Combine(directory, "response.raw"), cancellation.Token);
        var committedExchange = fresh with { ResponseBody = committedBody };

        var bodyMatches = Normalizer.NormalizeBody(committedExchange, @case)
            == Normalizer.NormalizeBody(fresh, @case);

        Console.WriteLine($"  {@case.Id}  {(bodyMatches ? "match" : "DIFFERS")}  {@case.Description}");
        if (!bodyMatches)
        {
            drifted.Add($"{@case.Id}: response body differs from the committed fixture");
        }
    }

    if (drifted.Count == 0)
    {
        Console.WriteLine("No drift.");
        return 0;
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("The broker no longer matches the committed fixtures:");
    foreach (var line in drifted)
    {
        Console.Error.WriteLine($"  {line}");
    }

    return 1;
}
