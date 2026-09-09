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

    /// <summary>
    /// A routable address, whether written plainly or encoded inside a token.
    /// </summary>
    /// <remarks>
    /// The address a sitting was taken from is not the broker's data; it is the recordist's.
    /// The broker puts it in the transaction token as <c>transaction_client_ip</c>, so it arrives
    /// without anyone choosing to write it down, and it survived three sittings before anybody
    /// looked - the scrubber replaced the signed token with a placeholder, and the decoded payload
    /// written beside it for readability kept the address in plain sight.
    /// <para>
    /// Shaped rather than listed, like the personal-number check above and for the same reason:
    /// the next address will be a different one. Loopback, the private ranges and the blocks RFC
    /// 5737 and RFC 3849 set aside for documentation are all allowed, because those describe
    /// nobody's network and are what an example should use.
    /// </para>
    /// </remarks>
    public static Finding FindRoutableIp(string candidate)
    {
        foreach (Match match in IpPattern().Matches(candidate))
        {
            if (IsRoutable(match.Value))
            {
                return new Finding(true, match.Value, "plain text");
            }
        }

        foreach (var (segment, decoded) in DecodedSegments(candidate))
        {
            foreach (Match match in IpPattern().Matches(decoded))
            {
                if (IsRoutable(match.Value))
                {
                    return new Finding(true, match.Value, $"inside base64url segment {segment[..8]}...");
                }
            }
        }

        return Finding.None;
    }

    /// <summary>Whether an address belongs to somebody rather than to an example.</summary>
    private static bool IsRoutable(string candidate)
    {
        if (!System.Net.IPAddress.TryParse(candidate, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();

        return (octets[0], octets[1], octets[2]) switch
        {
            (0, _, _) => false,                                  // "this network"
            (10, _, _) => false,                                 // private
            (127, _, _) => false,                                // loopback
            (169, 254, _) => false,                              // link-local
            (172, >= 16 and <= 31, _) => false,                   // private
            (192, 0, 2) => false,                                // TEST-NET-1, for documentation
            (192, 168, _) => false,                              // private
            (198, 51, 100) => false,                             // TEST-NET-2, for documentation
            (203, 0, 113) => false,                              // TEST-NET-3, for documentation
            (>= 224, _, _) => false,                             // multicast and reserved
            _ => true,
        };
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

    /// <summary>
    /// Four dotted octets, not sitting inside a longer run of digits, dots or letters.
    /// </summary>
    /// <remarks>
    /// The boundary is what keeps it off version numbers and off the dotted quads inside a
    /// longer identifier. It over-reports by design: <see cref="IsRoutable" /> is what decides,
    /// and it is the part that can be read and argued with.
    /// </remarks>
    [GeneratedRegex(@"(?<![0-9A-Za-z.])((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![0-9A-Za-z.])")]
    private static partial Regex IpPattern();

    [GeneratedRegex(@"([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]*)")]
    private static partial Regex JwsPattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]{16,}")]
    private static partial Regex Base64UrlSegmentPattern();
}
