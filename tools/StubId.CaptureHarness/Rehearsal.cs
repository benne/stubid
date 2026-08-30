using System.Net;

namespace StubId.CaptureHarness;

/// <summary>
/// Sends every step's authorize request to the live broker, without completing any of them.
/// </summary>
/// <remarks>
/// A malformed parameter shows up as the broker's error page rather than its login page, and
/// finding that out with an operator in the chair costs the sitting's momentum and possibly a
/// second sitting. Nothing here authenticates anybody: each request stops at the point where
/// a person would take over.
/// </remarks>
public static class Rehearsal
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler);

        var problems = 0;

        Console.WriteLine("Sending each step's authorize request. Nothing is completed.");
        Console.WriteLine();

        foreach (var @case in ManualCatalogue.All)
        {
            string url;
            try
            {
                (url, _, _) = Session.BuildAuthorize(@case);
            }
            catch (InvalidOperationException error)
            {
                Console.WriteLine($"  {@case.Id}  skipped   {error.Message.Split('.')[0]}");
                continue;
            }

            using var response = await client.GetAsync(url, ct);
            var location = response.Headers.Location?.ToString() ?? "";

            var (verdict, note) = Classify(response.StatusCode, location, @case);
            Console.WriteLine($"  {@case.Id}  {verdict,-8}  {@case.Title}{note}");

            if (verdict == "PROBLEM")
            {
                problems++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0
            ? "Every step reaches the point where a person takes over."
            : $"{problems} step(s) refused before reaching the login page. Fix before sitting down.");

        return problems == 0 ? 0 : 1;
    }

    private static (string Verdict, string Note) Classify(
        HttpStatusCode status, string location, ManualCase @case)
    {
        if (location.Contains("/Account/Login", StringComparison.Ordinal))
        {
            return ("ready", "");
        }

        if (location.Contains("/Error?errorId=", StringComparison.Ordinal))
        {
            // The one step that is supposed to be refused: it exists to record the refusal.
            return @case.RedirectUriOverride is not null
                ? ("ready", "  (refused, which is the recording)")
                : ("PROBLEM", "  refused before the login page");
        }

        return ("PROBLEM", $"  unexpected: {(int)status} {location}");
    }
}
