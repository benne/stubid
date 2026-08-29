using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>Where something sensitive was found, and in what.</summary>
/// <param name="Found">True when there is something to act on.</param>
/// <param name="Value">The offending text.</param>
/// <param name="Location">
/// Plain text, or a description of the encoding it was hiding inside. A finding inside a
/// token is the one that matters: it is invisible to anyone reading the file.
/// </param>
public readonly record struct Finding(bool Found, string Value, string Location)
{
    public static readonly Finding None = new(false, "", "");
}

/// <summary>
/// Detects content that must not reach the repository, including content that is encoded
/// rather than written out.
/// </summary>
/// <remarks>
/// Both checks here replaced ones that looked right and did nothing. Scanning the text as
/// written is not enough: a recorded login carries JSON Web Tokens, and a base64url segment
/// is one long alphanumeric run, so a personal number inside a token matched no pattern and
/// shipped with a green build.
/// </remarks>
public static partial class SensitiveContent
{
    /// <summary>
    /// A personal number, whether written plainly, hyphenated, or encoded inside a token.
    /// </summary>
    public static Finding FindCpr(string candidate)
    {
        foreach (Match match in CprPattern().Matches(candidate))
        {
            if (CouldBeSomeonesBirthday(match))
            {
                return new Finding(true, match.Value, "plain text");
            }
        }

        foreach (var (segment, decoded) in DecodedSegments(candidate))
        {
            foreach (Match match in CprPattern().Matches(decoded))
            {
                if (CouldBeSomeonesBirthday(match))
                {
                    return new Finding(true, match.Value, $"inside base64url segment {segment[..8]}...");
                }
            }
        }

        return Finding.None;
    }

    /// <summary>
    /// Whether the leading six digits could be a real date of birth.
    /// </summary>
    /// <remarks>
    /// A personal number encodes a real birthday, so one opening with the 31st of February
    /// was never issued to anyone. That gives documentation and tests a way to show the
    /// shape of a personal number without printing something that is probably somebody's:
    /// roughly ten million are in use, so a plausible date plus four digits has a high
    /// chance of belonging to a real person. Replacement numbers, which raise the day into
    /// the 61-91 range, never reach here because the pattern does not match them.
    /// </remarks>
    private static bool CouldBeSomeonesBirthday(Match match)
    {
        var day = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

        var longestPossible = month switch
        {
            2 => 29,                          // in a leap year
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };

        return day <= longestPossible;
    }

    /// <summary>
    /// A JSON Web Token, found by structure rather than by how its header happens to begin.
    /// </summary>
    /// <remarks>
    /// The previous check tested for the literal "eyJhbGciOi", which is base64url for a
    /// header whose JSON starts with the alg member. A header starting with typ encodes to
    /// something else entirely and passed straight through — and the transaction token comes
    /// from a different subsystem whose header member order is one of the things nobody has
    /// observed yet, so the old check was most likely to miss the one token nobody has seen.
    /// </remarks>
    public static Finding FindSignedToken(string candidate)
    {
        foreach (Match match in JwsPattern().Matches(candidate))
        {
            if (!TryDecode(match.Groups[1].Value, out var header))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(header);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("alg", out _))
                {
                    return new Finding(true, match.Value[..Math.Min(24, match.Value.Length)], "compact JWS");
                }
            }
            catch (JsonException)
            {
                // A run of base64url-looking text that is not a token. Nothing to report.
            }
        }

        return Finding.None;
    }

    private static IEnumerable<(string Segment, string Decoded)> DecodedSegments(string candidate)
    {
        foreach (Match match in Base64UrlSegmentPattern().Matches(candidate))
        {
            if (TryDecode(match.Value, out var decoded))
            {
                yield return (match.Value, decoded);
            }
        }
    }

    private static bool TryDecode(string segment, out string decoded)
    {
        decoded = "";
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(segment);
            var text = Encoding.UTF8.GetString(bytes);

            // Only interested in text that decoded into something readable; random bytes that
            // happen to decode are noise.
            if (text.Any(char.IsControl) && !text.Any(c => c is '\n' or '\r' or '\t'))
            {
                return false;
            }

            decoded = text;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ten digits opening with a plausible day and month, optionally separated after the
    /// sixth, and not sitting inside a longer alphanumeric run.
    ///
    /// The boundary keeps it from matching certificate thumbprints: one of the broker's
    /// contains a ten-digit run that reads as a date in July. The optional separator is
    /// there because the six-four form with a hyphen
    /// is how the number is often written, and an exact-string redaction of the unseparated
    /// form does not match it.
    ///
    /// It over-reports on purpose. Replacement numbers use a day of 61-91 and never match.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z])(0[1-9]|[12]\d|3[01])(0[1-9]|1[0-2])\d{2}[- ]?\d{4}(?![0-9A-Za-z])")]
    private static partial Regex CprPattern();

    [GeneratedRegex(@"([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]*)")]
    private static partial Regex JwsPattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]{16,}")]
    private static partial Regex Base64UrlSegmentPattern();
}
