using StubId.CaptureHarness;

// Records the broker exchanges that StubID's fidelity tests assert against.
//
//   capture   record every case and write the fixtures
//   verify    record again and compare against what is committed
//
// Both hit the broker's public pre-production environment with unauthenticated requests.

var command = args.Length > 0 ? args[0] : "capture";
var root = args.Length > 1
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "neb", "pp");
root = Path.GetFullPath(root);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var store = new FixtureStore(root);
using var recorder = new Recorder();
var cases = CaptureCatalogue.All;

switch (command)
{
    case "capture":
        return await CaptureAsync();
    case "verify":
        return await VerifyAsync();
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'capture' or 'verify'.");
        return 2;
}

async Task<int> CaptureAsync()
{
    Console.WriteLine($"Recording {cases.Count} cases into {root}");
    var surprises = new List<string>();

    foreach (var @case in cases)
    {
        var exchange = await recorder.RecordAsync(@case, cancellation.Token);
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

    await store.WriteManifestAsync(
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), cancellation.Token);

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
