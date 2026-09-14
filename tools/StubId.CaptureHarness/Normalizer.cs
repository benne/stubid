using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Masks the parts of a recording that legitimately differ between two identical requests,
/// so a re-run can be compared against what is committed.
/// </summary>
/// <remarks>
/// This is where a fixture states what it is actually promising. Anything masked here is a
/// value StubID does not have to reproduce; anything left alone is a value it does. Masking
/// too much turns a fidelity test into a shape test that passes on nearly anything.
/// </remarks>
public static class Normalizer
{
    /// <summary>Headers that differ on every request regardless of the case.</summary>
    private static readonly string[] AlwaysVolatile =
    [
        "Date", "Set-Cookie", "Age", "Server-Timing", "Request-Context",
    ];

    public static string NormalizeHead(RecordedExchange exchange, CaptureCase @case)
    {
        var volatileHeaders = AlwaysVolatile
            .Concat(@case.VolatileHeaders)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.Append(exchange.StatusCode).Append(' ').AppendLine(exchange.ReasonPhrase ?? "");

        foreach (var (name, value) in exchange.ResponseHeaders.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            var masked = volatileHeaders.Contains(name) ? "<volatile>" : value;
            builder.Append(name).Append(": ").AppendLine(masked);
        }

        return Mask(builder.ToString(), @case.VolatileBodyPatterns);
    }

    public static string NormalizeBody(RecordedExchange exchange, CaptureCase @case) =>
        Mask(Sorted(Encoding.UTF8.GetString(exchange.ResponseBody), @case.UnorderedArrays), @case.VolatileBodyPatterns);

    /// <summary>Each named array of strings, sorted, so its order stops mattering and its entries do not.</summary>
    /// <remarks>An array that is not all strings is left as it was, and so still compared in order.</remarks>
    private static string Sorted(string text, IReadOnlyList<string> members)
    {
        foreach (var member in members)
        {
            text = Regex.Replace(
                text,
                $@"(""{Regex.Escape(member)}""\s*:\s*)(\[[^\]]*\])",
                match =>
                {
                    try
                    {
                        var entries = JsonSerializer.Deserialize<string[]>(match.Groups[2].Value);
                        if (entries is null)
                        {
                            return match.Value;
                        }

                        Array.Sort(entries, StringComparer.Ordinal);
                        return match.Groups[1].Value + JsonSerializer.Serialize(entries);
                    }
                    catch (JsonException)
                    {
                        return match.Value;
                    }
                },
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
        }

        return text;
    }

    /// <summary>Whether a fresh recording says what the committed one does.</summary>
    /// <remarks>
    /// The fresh body is scrubbed first, the way the writer scrubbed the committed one. Compared as
    /// served, every body naming a configured value drifted on every run. The first broker's
    /// unattended bodies name none, so nothing showed; Signicat's discovery document names the
    /// tenant in every URL it holds.
    /// </remarks>
    public static bool BodyMatches(byte[] committed, RecordedExchange fresh, CaptureCase @case) =>
        NormalizeBody(fresh with { ResponseBody = committed }, @case)
        == NormalizeBody(fresh with { ResponseBody = FixtureStore.ScrubBody(fresh.ResponseBody) }, @case);

    private static string Mask(string text, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            text = Regex.Replace(text, pattern, "<volatile>", RegexOptions.None, TimeSpan.FromSeconds(5));
        }

        return text;
    }
}
