using System.Text;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Keeps the values that identify an account out of the fixtures, and puts them back when a
/// case is replayed.
/// </summary>
/// <remarks>
/// <para>
/// Nets eID Broker publishes credentials for its open test clients so anyone can exercise
/// pre-production, so those are not confidential. They are still kept out of the repository
/// and out of the fixtures. Something secret-shaped in a recorded exchange trips every
/// scanner pointed at a public repository, and "that one is published on purpose" is not an
/// argument anyone should have to have twice.
/// </para>
/// <para>
/// Supply the value through the environment when recording, or in <c>capture.local.json</c>.
/// The broker's own documentation is where to get the published ones.
/// </para>
/// </remarks>
public static partial class Scrubber
{
    /// <summary>
    /// Everything read from the local configuration and replaced on its way into a fixture,
    /// with the broker it belongs to and the placeholder that stands in for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every private client belongs on this list: what separates one that is scrubbed from one
    /// that is not is only whether somebody wrote the line. That was true of the nine entries the
    /// first broker needed, and it is why <see cref="Preflight" /> reads this list rather than
    /// keeping a second copy of the names — the copy it used to keep is exactly the kind of thing
    /// a tenth entry would have been left out of.
    /// </para>
    /// <para>
    /// Not all of them are credentials, whatever the name says. Signicat's tenant is its
    /// hostname, so the account's own subdomain appears in the issuer, in every absolute URL in
    /// the discovery document and in <c>iss</c> in every token, and the key identifier names a
    /// signing key registered on one client. Neither is secret. Both are somebody's account
    /// rather than the broker's protocol, which is the same reason a client identifier is here
    /// beside the secrets.
    /// </para>
    /// <para>
    /// Registering the domain is also what makes a Signicat recording refuse rather than leak: a
    /// case whose template names <c>{{SIGNICAT_DOMAIN}}</c> cannot be sent at all until the
    /// setting resolves, because <see cref="Unscrub(string)" /> throws instead of putting a
    /// placeholder on the wire.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(Broker Broker, string Placeholder, string Setting)> Credentials =
    [
        (Broker.NetsEidBroker, "{{NEB_PP_OPEN_CLIENT_CODE_SECRET}}", "STUBID_NEB_PP_CODE_CLIENT_SECRET"),
        (Broker.NetsEidBroker, "{{NEB_PP_CLIENT_ID}}", "STUBID_NEB_PP_CLIENT_ID"),
        (Broker.NetsEidBroker, "{{NEB_PP_CLIENT_SECRET}}", "STUBID_NEB_PP_CLIENT_SECRET"),

        // The single sign-on and hybrid registrations.
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_A_CLIENT_ID}}", "STUBID_NEB_PP_SSO_A_CLIENT_ID"),
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_A_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_A_CLIENT_SECRET"),
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_B_CLIENT_ID}}", "STUBID_NEB_PP_SSO_B_CLIENT_ID"),
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_B_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_B_CLIENT_SECRET"),
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_C_CLIENT_ID}}", "STUBID_NEB_PP_SSO_C_CLIENT_ID"),
        (Broker.NetsEidBroker, "{{NEB_PP_SSO_C_CLIENT_SECRET}}", "STUBID_NEB_PP_SSO_C_CLIENT_SECRET"),

        // The tenant first, because it is the one with no equivalent on the first broker and the
        // one that reaches every byte of every recording.
        (Broker.Signicat, "{{SIGNICAT_DOMAIN}}", "STUBID_SIGNICAT_DOMAIN"),
        (Broker.Signicat, "{{SIGNICAT_CLIENT_ID}}", "STUBID_SIGNICAT_CLIENT_ID"),
        (Broker.Signicat, "{{SIGNICAT_CLIENT_SECRET}}", "STUBID_SIGNICAT_CLIENT_SECRET"),
        (Broker.Signicat, "{{SIGNICAT_KEY_ID}}", "STUBID_SIGNICAT_KEY_ID"),
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
    /// Every value this machine can recognize, as the name to report it under paired with the
    /// value itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the scrubber replaces on the way into a fixture is exactly what a guard should look
    /// for in one, so both read the same list. The difference is direction: the scrubber runs
    /// while a recording is being written and sees only that recording, and the guard runs over
    /// everything committed, including the documents somebody typed by hand — which is where a
    /// hostname or a client identifier actually gets written down.
    /// </para>
    /// <para>
    /// It returns nothing on a machine that has never been configured to record, which includes
    /// every machine CI runs on. That is not a gap to apologize for: nobody can search for a
    /// value they do not have, and the people who have them are the only ones who can leak them.
    /// </para>
    /// </remarks>
    public static IEnumerable<(string Name, string Value)> Configured() =>
        Configured(LocalSettings.Get, LocalSettings.Redactions().Select(e => (e.Key, e.Value)));

    /// <inheritdoc cref="Configured()"/>
    /// <remarks>
    /// With both sources passed in, for the same reason the scrubbing overload takes them: a test
    /// that reads the machine passes on a fresh checkout and fails once somebody configures
    /// theirs to record.
    /// </remarks>
    public static IEnumerable<(string Name, string Value)> Configured(
        Func<string, string?> resolve,
        IEnumerable<(string Placeholder, string Value)> redactions) =>
    [
        .. Credentials
            .Select(entry => (entry.Setting, Value: resolve(entry.Setting)))
            .Where(entry => !string.IsNullOrEmpty(entry.Value))
            .Select(entry => (entry.Setting, entry.Value!)),

        // Keyed by the placeholder, which is the only name a redaction has.
        .. redactions.Where(entry => !string.IsNullOrEmpty(entry.Value)),
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
        foreach (var (_, placeholder, setting) in Credentials)
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

        foreach (var (_, placeholder, setting) in Credentials)
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
