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
    ];

    /// <summary>
    /// Substitutes real credentials into a value about to be sent. Throws rather than
    /// sending a placeholder to the broker, which would record a confusing 400 instead of
    /// the exchange the case is meant to capture.
    /// </summary>
    public static string Unscrub(string text)
    {
        foreach (var (placeholder, setting) in Credentials)
        {
            if (!text.Contains(placeholder, StringComparison.Ordinal))
            {
                continue;
            }

            var value = LocalSettings.Get(setting);
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
    /// organisation, and a fixture is not the place for a company's CVR number.
    /// </remarks>
    public static string Scrub(string text)
    {
        foreach (var (placeholder, setting) in Credentials)
        {
            var value = LocalSettings.Get(setting);
            if (!string.IsNullOrEmpty(value))
            {
                text = text.Replace(value, placeholder, StringComparison.Ordinal);
                text = text.Replace(Uri.EscapeDataString(value), placeholder, StringComparison.Ordinal);
            }
        }

        foreach (var (placeholder, value) in LocalSettings.Redactions())
        {
            if (!string.IsNullOrEmpty(value))
            {
                text = text.Replace(value, placeholder, StringComparison.Ordinal);
                text = text.Replace(Uri.EscapeDataString(value), placeholder, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// Finds anything shaped like a Danish CPR number. Used by the fixture guard rather than
    /// for scrubbing: nothing should contain one at all, and if something does the build
    /// should stop rather than quietly mask it.
    /// </summary>
    public static Match FindCprShapedText(string candidate) => CprPattern().Match(candidate);

    /// <summary>
    /// Finds a form field carrying something other than a placeholder where a credential
    /// belongs. This is the check that would have caught a real secret reaching a fixture,
    /// and unlike a list of known secrets it also catches the ones nobody thought of.
    /// </summary>
    public static Match FindUnscrubbedCredential(string candidate) =>
        UnscrubbedCredentialPattern().Match(candidate);

    /// <summary>
    /// Ten digits opening with a plausible day and month, standing alone rather than sitting
    /// inside a longer alphanumeric run.
    ///
    /// The boundary is not fussiness. Certificate thumbprints are long hex strings and one
    /// of the broker's contains "3007045896", which reads as the 30th of July. A real CPR in
    /// a recording appears as its own value - "cpr":"..." or cpr=... - so requiring it to
    /// stand alone keeps the check useful instead of permanently red.
    ///
    /// It still over-reports on purpose. A false positive costs a minute; a miss is a
    /// disclosure. Replacement numbers use a day of 61-91 and never match.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z])(0[1-9]|[12]\d|3[01])(0[1-9]|1[0-2])\d{6}(?![0-9A-Za-z])")]
    private static partial Regex CprPattern();

    // A credential-bearing field whose value is neither a placeholder nor obviously inert.
    // Covers both shapes a body can take: form encoding and JSON. Percent-encoded braces are
    // how a placeholder looks once a form has been encoded.
    [GeneratedRegex(
        @"(client_secret|password|assertion)""?\s*[:=]\s*""?(?!%7B%7B|\{\{|wrong-|not-a-)[^&"",\s}]{12,}")]
    private static partial Regex UnscrubbedCredentialPattern();
}
