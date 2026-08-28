using System.Text;
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
public static class Normaliser
{
    /// <summary>Headers that differ on every request regardless of the case.</summary>
    private static readonly string[] AlwaysVolatile =
    [
        "Date", "Set-Cookie", "Age", "Server-Timing", "Request-Context",
    ];

    public static string NormaliseHead(RecordedExchange exchange, CaptureCase @case)
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

    public static string NormaliseBody(RecordedExchange exchange, CaptureCase @case) =>
        Mask(Encoding.UTF8.GetString(exchange.ResponseBody), @case.VolatileBodyPatterns);

    private static string Mask(string text, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            text = Regex.Replace(text, pattern, "<volatile>", RegexOptions.None, TimeSpan.FromSeconds(5));
        }

        return text;
    }
}
