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
    /// The address a sitting is taken from is not the broker's data; it is the recordist's.
    /// The broker puts it in the transaction token as <c>transaction_client_ip</c>, so it arrives
    /// without anyone choosing to write it down. Replacing a signed token with a placeholder does
    /// not cover it either: a decoded payload written beside the token for readability carries the
    /// same claim in plain sight.
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

    /// <summary>
    /// A hostname that names the account a recording was taken from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first broker made this a problem nobody had. Its pre-production host is public and
    /// shared, so no recording of it could say whose it was. Signicat's tenant is the hostname,
    /// which puts the account's own subdomain in the issuer, in every absolute URL inside the
    /// discovery document, and in <c>iss</c> in every token a sitting produces.
    /// </para>
    /// <para>
    /// Shaped rather than listed, like the personal-number and address checks above, and for a
    /// sharper reason than either: a subdomain is a word somebody chose, so nothing about the
    /// value itself says it is an account name. Only the suffix does. Checking the suffix is what
    /// makes this guard work on a machine that has never been configured to record — including
    /// every machine CI runs on, which is where a document written by hand would otherwise reach
    /// the repository unread.
    /// </para>
    /// <para>
    /// Only the sandbox suffix, because it is the only tenant-shaped host any of this project's
    /// evidence establishes. What a production tenant is called has not been observed, and a
    /// guard written against a guess would read as coverage without being any.
    /// </para>
    /// </remarks>
    public static Finding FindTenantHost(string candidate)
    {
        foreach (Match match in TenantHostPattern().Matches(candidate))
        {
            return new Finding(true, match.Value, "plain text");
        }

        foreach (var (segment, decoded) in DecodedSegments(candidate))
        {
            foreach (Match match in TenantHostPattern().Matches(decoded))
            {
                return new Finding(true, match.Value, $"inside base64url segment {segment[..8]}...");
            }
        }

        return Finding.None;
    }

    /// <summary>
    /// The shortest value worth searching a whole repository for.
    /// </summary>
    /// <remarks>
    /// A configured value is matched as a substring, so a short one matches text that has nothing
    /// to do with it - and a guard that cries wolf gets switched off, which costs more than the
    /// one file it was going to catch. Anything below this is reported by <see cref="Preflight" />
    /// as not scanned rather than quietly dropped here.
    /// </remarks>
    public const int ShortestScannableValue = 6;

    /// <summary>
    /// Anything the local configuration knows the value of, wherever it appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other checks here find things by their shape, which is what lets them run everywhere.
    /// This one finds what has no shape: a client identifier, a key identifier, an organization's
    /// name, an account word written into a sentence. None of those can be recognized without
    /// being known, so this check only sees as much as the machine running it is configured to
    /// see - and that is the whole population that can leak them.
    /// </para>
    /// <para>
    /// The finding names the setting rather than the value. A failure message is read in a
    /// terminal, pasted into an issue and kept in a build log, and none of those is a place to
    /// put the thing the check exists to keep out of one file.
    /// </para>
    /// </remarks>
    /// <param name="candidate">The text to search.</param>
    /// <param name="configured">
    /// What to look for, as the name to report paired with the value to find.
    /// </param>
    public static Finding FindConfigured(
        string candidate,
        IEnumerable<(string Name, string Value)> configured)
    {
        var looking = Scannable(configured);

        foreach (var (name, value) in looking)
        {
            // Case-insensitively, which is wider than the scrubber's exact replace on purpose:
            // a hostname does not care about case and a sentence capitalizes a name at its start,
            // and this check is meant to catch what the scrubber could not.
            if (candidate.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return new Finding(true, name, "plain text");
            }
        }

        foreach (var (segment, decoded) in DecodedSegments(candidate))
        {
            foreach (var (name, value) in looking)
            {
                if (decoded.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    return new Finding(true, name, $"inside base64url segment {segment[..8]}...");
                }
            }
        }

        return Finding.None;
    }

    /// <summary>
    /// The values long enough to search for, each also in the form a URL would carry it in.
    /// </summary>
    /// <remarks>
    /// The escaped form matters for the same reason it does in the scrubber: the first capture
    /// run wrote a credential into a fixture because the replace ran after the form had been
    /// percent-encoded, and a value with a slash or a space in it is a different string by then.
    /// </remarks>
    private static (string Name, string Value)[] Scannable(
        IEnumerable<(string Name, string Value)> configured) =>
        [.. configured
            .Where(entry => entry.Value.Length >= ShortestScannableValue)
            .SelectMany(entry => Uri.EscapeDataString(entry.Value) == entry.Value
                ? new[] { entry }
                : [entry, (entry.Name, Uri.EscapeDataString(entry.Value))])];

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

    /// <summary>
    /// A DNS label under Signicat's sandbox suffix, not sitting inside a longer name.
    /// </summary>
    /// <remarks>
    /// The boundary is what lets a placeholder through without an exemption: neither
    /// <c>{{SIGNICAT_DOMAIN}}</c> nor <c>&lt;subdomain&gt;</c> ends in a character a DNS label may
    /// contain, so documentation can go on writing the URL in full.
    /// </remarks>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.-])[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.sandbox\.signicat\.com",
        RegexOptions.IgnoreCase)]
    private static partial Regex TenantHostPattern();

    [GeneratedRegex(@"([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]{16,})\.([A-Za-z0-9_-]*)")]
    private static partial Regex JwsPattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]{16,}")]
    private static partial Regex Base64UrlSegmentPattern();
}
