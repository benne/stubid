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
//   sanitise  reprocess a written session with the current rules
//
// Both hit the broker's public pre-production environment with unauthenticated requests.

var command = args.Length > 0 ? args[0] : "capture";

// Recording a case rewrites its bytes, and an error id or a timestamp differs every time. So
// adding a case to the catalogue would otherwise churn every fixture beside it, burying the
// one new recording in nineteen diffs that say nothing.
var only = args
    .FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?[7..]
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var root = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "neb", "pp");
root = Path.GetFullPath(root);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var store = new FixtureStore(root);
using var recorder = new Recorder();
var cases = only is null
    ? CaptureCatalogue.All
    : [.. CaptureCatalogue.All.Where(c => only.Contains(c.Id, StringComparer.OrdinalIgnoreCase))];

// The two catalogues are filtered separately, because a name in one is not in the other: the
// sitting's steps are deliberately not in CaptureCatalogue, and --only=CAP-031 matching
// nothing there is the normal case rather than a mistake.
var manual = ManualCatalogue.Selected(only);

switch (command)
{
    case "capture":
        return Refuse(cases) ?? await CaptureAsync();
    case "verify":
        return Refuse(cases) ?? await VerifyAsync();
    case "rehearse":
        return Refuse(manual) ?? await Rehearsal.RunAsync(manual, cancellation.Token);
    case "sanitise":
        return await Sanitise.RunAsync(new FixtureStore(
            Path.GetFullPath(Path.Combine(root, "..", "..", "..", "fixtures", "neb", "pp-session"))),
            cancellation.Token);
    case "check":
        return await Preflight.RunAsync(cancellation.Token);
    case "session":
        // The manual sitting writes into its own directory: the unattended pack must stay
        // reproducible by re-running capture, and these recordings never are.
        return Refuse(manual) ?? await Session.RunAsync(
            new FixtureStore(Path.GetFullPath(
                Path.Combine(root, "..", "..", "..", "fixtures", "neb", "pp-session"))),
            manual);
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'capture', 'verify', "
            + "'session', 'rehearse', 'sanitise' or 'check'.");
        return 2;
}

int? Refuse<T>(IReadOnlyList<T> selected)
{
    if (selected.Count > 0)
    {
        return null;
    }

    Console.Error.WriteLine("No case matched --only.");
    return 2;
}

async Task<int> CaptureAsync()
{
    Console.WriteLine($"Recording {cases.Count} cases into {root}");
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

        if (@case.Id == "CAP-002")
        {
            var reportPath = Path.Combine(root, "..", "certificates.md");
            await File.WriteAllTextAsync(
                Path.GetFullPath(reportPath),
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
        Console.Error.WriteLine("The broker answered differently than the catalogue expects:");
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
    Console.WriteLine($"Re-recording {cases.Count} cases and comparing against {root}");
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

        var bodyMatches = Normaliser.NormaliseBody(committedExchange, @case)
            == Normaliser.NormaliseBody(fresh, @case);

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
