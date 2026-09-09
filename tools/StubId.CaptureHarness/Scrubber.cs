using System.Text;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Keeps credentials out of the fixtures, and puts them back when a case is replayed.
/// </summary>
/// <remarks>
/// <para>
/// The broker publishes credentials for its open test clients so anyone can exercise
/// pre-production, so these are not confidential. They are still kept out of the repository
/// and out of the fixtures. Something secret-shaped in a recorded exchange trips every
/// scanner pointed at a public repository, and "that one is published on purpose" is not an
/// argument anyone should have to have twice.
/// </para>
/// <para>
/// Supply the value through the environment when recording. The broker's own documentation
/// is where to get it.
/// </para>
/// </remarks>
public static partial class Scrubber
{
    private static readonly (string Placeholder, string Setting)[] Credentials =
    [
        ("{{NEB_PP_OPEN_CLIENT_CODE_SECRET}}", "STUBID_NEB_PP_CODE_CLIENT_SECRET"),
        ("{{NEB_PP_CLIENT_ID}}", "STUBID_NEB_PP_CLIENT_ID"),
        ("{{NEB_PP_CLIENT_SECRET}}", "STUBID_NEB_PP_CLIENT_SECRET"),

        // The single sign-on and hybrid registrations. Every private client belongs on this
        // list: what separates one that is scrubbed from one that is not is only whether somebody
        // wrote the line.
        ("{{NEB_PP_SSO_A_CLIENT_ID}}", "STUBID_NEB_PP_SSO_A_CLIENT_ID"),
        ("{{NEB_PP_SSO_A_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_A_CLIENT_SECRET"),
        ("{{NEB_PP_SSO_B_CLIENT_ID}}", "STUBID_NEB_PP_SSO_B_CLIENT_ID"),
        ("{{NEB_PP_SSO_B_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_B_CLIENT_SECRET"),
        ("{{NEB_PP_SSO_C_CLIENT_ID}}", "STUBID_NEB_PP_SSO_C_CLIENT_ID"),
        ("{{NEB_PP_SSO_C_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_C_CLIENT_SECRET"),
    ];

    /// <summary>
    /// Claims that describe the client rather than the broker, blanked wherever they appear.
    /// </summary>
    /// <remarks>
    /// By name and not by value, which is what makes these different from everything else here.
    /// A redaction keyed on a value needs somebody to know the value first, and neither of these
    /// is knowable in advance: an address can be handed out fresh by a router, and the distance is
    /// computed per sitting. A rule written against either would be right once.
    /// <para>
    /// Blanking by name is safe precisely because StubID reproduces neither. The address it serves
    /// is the one the calling request arrived from, and the distance is a constant, so no recorded
    /// value is load-bearing - what a recording has to preserve is that the claim is there, in that
    /// slot, as a string.
    /// </para>
    /// </remarks>
    private static readonly (string Claim, string Placeholder)[] ClientClaims =
    [
        ("transaction_client_ip", "{{CLIENT_IP}}"),
        ("mitid.geo_ip_distance_km", "{{GEO_IP_DISTANCE_KM}}"),
    ];

    /// <summary>
    /// Substitutes real credentials into a value about to be sent. Throws rather than
    /// sending a placeholder to the broker, which would record a confusing 400 instead of
    /// the exchange the case is meant to capture.
    /// </summary>
    public static string Unscrub(string text) => Unscrub(text, LocalSettings.Get);

    /// <summary>
    /// Takes the settings resolver so the behavior can be tested without depending on
    /// whatever happens to be configured on the machine running the tests.
    /// </summary>
    public static string Unscrub(string text, Func<string, string?> resolve)
    {
        foreach (var (placeholder, setting) in Credentials)
        {
            if (!text.Contains(placeholder, StringComparison.Ordinal))
            {
                continue;
            }

            var value = resolve(setting);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"Set {setting} to record this case, in the environment or in "
                    + "capture.local.json at the repository root. Credentials are kept out of "
                    + "this repository on purpose.");
            }

            text = text.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Puts placeholders back before a recording is written.
    /// </summary>
    /// <remarks>
    /// Applies to responses as well as requests, because the broker echoes things. A valid
    /// authorize request redirects to a login URL carrying the client_id, so recording with a
    /// private client would publish it without this. Anything listed under "redact" in
    /// capture.local.json is replaced too: a transaction token names the receiving
    /// organization, and a fixture is not the place for a company's CVR number.
    /// </remarks>
    public static string Scrub(string text) => Scrub(text, LocalSettings.Get);

    /// <inheritdoc cref="Scrub(string)"/>
    public static string Scrub(string text, Func<string, string?> resolve) =>
        Scrub(text, resolve, LocalSettings.Redactions().Select(e => (e.Key, e.Value)));

    /// <summary>
    /// With the redactions passed in rather than read from the machine.
    /// </summary>
    /// <remarks>
    /// The same reason the resolver is injected: a test that reads the local configuration passes
    /// on a fresh checkout and fails once somebody configures their machine to actually record.
    /// </remarks>
    public static string Scrub(
        string text,
        Func<string, string?> resolve,
        IEnumerable<(string Placeholder, string Value)> redactions)
    {
        // First, and without consulting any configuration: these two are blanked by name.
        text = ClientClaimPattern().Replace(text, match =>
        {
            var placeholder = ClientClaims
                .First(claim => claim.Claim == match.Groups[1].Value)
                .Placeholder;

            return $"\"{match.Groups[1].Value}\":\"{placeholder}\"";
        });

        foreach (var (placeholder, setting) in Credentials)
        {
            var value = resolve(setting);
            if (!string.IsNullOrEmpty(value))
            {
                text = text.Replace(value, placeholder, StringComparison.Ordinal);
                text = text.Replace(Uri.EscapeDataString(value), placeholder, StringComparison.Ordinal);
            }
        }

        var redacting = redactions as (string Placeholder, string Value)[] ?? [.. redactions];

        foreach (var (placeholder, value) in redacting)
        {
            if (!string.IsNullOrEmpty(value))
            {
                text = text.Replace(value, placeholder, StringComparison.Ordinal);
                text = text.Replace(Uri.EscapeDataString(value), placeholder, StringComparison.Ordinal);
            }
        }

        return ScrubEncoded(text, resolve, redacting);
    }

    /// <summary>
    /// The same replacements again, inside base64 the broker sends as a claim value.
    /// </summary>
    /// <remarks>
    /// A plain string replace cannot reach these. Base64 encodes three bytes at a time, so the
    /// same name sitting at a different offset in a sentence produces a different substring, and
    /// there is nothing stable to search for. The value has to be decoded, scrubbed and encoded
    /// again.
    /// <para>
    /// This is not hypothetical either. The CPR consent text is a base64 sentence naming the
    /// organization that receives the number, and the redact block has carried an entry for that
    /// name from the beginning - it simply never matched, because the rule and the value it was
    /// written for sat one encoding apart.
    /// </para>
    /// </remarks>
    private static string ScrubEncoded(
        string text,
        Func<string, string?> resolve,
        (string Placeholder, string Value)[] redactions)
    {
        foreach (Match match in Base64ValuePattern().Matches(text))
        {
            var encoded = match.Groups[1].Value;

            if (!TryDecodeText(encoded, out var decoded))
            {
                continue;
            }

            // Recurses once: the inner call has nothing base64 left to find, because a decoded
            // sentence is plain text.
            var scrubbed = Scrub(decoded, resolve, redactions);

            if (scrubbed != decoded)
            {
                text = text.Replace(
                    encoded,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(scrubbed)),
                    StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>Whether a run of base64 decodes to text somebody could read.</summary>
    private static bool TryDecodeText(string encoded, out string decoded)
    {
        decoded = "";

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

            if (text.Length == 0 || text.Any(char.IsControl))
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
    /// Finds a form or JSON field carrying something other than a placeholder where a
    /// credential belongs. Checks the shape rather than a list of known secrets, so it also
    /// catches the ones nobody thought to add to the list.
    /// </summary>
    public static Match FindUnscrubbedCredential(string candidate) =>
        UnscrubbedCredentialPattern().Match(candidate);

    /// <summary>
    /// Either claim that describes the client, with whatever value it was given.
    /// </summary>
    /// <remarks>
    /// The value alternative accepts a number as well as a string, because a broker that stopped
    /// quoting one of these would otherwise slip through a pattern written for the form it happens
    /// to send today.
    /// </remarks>
    [GeneratedRegex("\"(transaction_client_ip|mitid\\.geo_ip_distance_km)\"\\s*:\\s*(\"[^\"]*\"|[-0-9.]+)")]
    private static partial Regex ClientClaimPattern();

    /// <summary>A JSON string value that is entirely base64, which is how a claim carries text.</summary>
    [GeneratedRegex("\"([A-Za-z0-9+/]{16,}={0,2})\"")]
    private static partial Regex Base64ValuePattern();

    // A credential-bearing field whose value is neither a placeholder nor obviously inert.
    // Covers both shapes a body can take: form encoding and JSON. Percent-encoded braces are
    // how a placeholder looks once a form has been encoded.
    [GeneratedRegex(
        @"(client_secret|password|assertion)""?\s*[:=]\s*""?(?!%7B%7B|\{\{|wrong-|not-a-)[^&"",\s}]{12,}")]
    private static partial Regex UnscrubbedCredentialPattern();
}
