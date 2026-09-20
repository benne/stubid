using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Takes the identity token out of a recorded logout URL.
/// </summary>
/// <remarks>
/// <para>
/// End session names the session it is ending by carrying that session's token in the query, so
/// the URL a fixture would record holds a compact JWS. The guard rejects one, and one has reached
/// a fixture twice already. It cannot be scrubbed in place either: changing a byte inside a JWS
/// invalidates the signature, and re-signing produces bytes nobody sent. So the position is held
/// by a placeholder and the decoded halves are written beside the exchange, which is what happens
/// to a token in a response body.
/// </para>
/// <para>
/// The difference from <see cref="RequestObject.StripFrom" /> is whose token it is. A request
/// object is ours, and is recorded unchecked because a signature we can still recompute says
/// nothing worth recording. A hint is the broker's, so it is checked against the key set the
/// sitting fetched - and an unresolved kid records null, unchecked rather than failed, because
/// the second broker answers with a different subset of its keys on every request.
/// </para>
/// </remarks>
public static partial class IdTokenHint
{
    /// <summary>The parameter this strips, which also names the placeholder that replaces it.</summary>
    public const string Parameter = "id_token_hint";

    /// <summary>
    /// Replaces a hint in a recorded URL with a placeholder, and hands back what it replaced so
    /// the header and payload can be written beside the exchange.
    /// </summary>
    /// <param name="jwks">
    /// The key set as the sitting fetched it, or null where none was. Null records the signature
    /// as unchecked, which is what a sitting with no key set honestly has.
    /// </param>
    public static (string Url, ExtractedToken? Hint) StripFrom(string url, string? jwks = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        var match = HintParameter().Match(url);
        if (!match.Success)
        {
            return (url, null);
        }

        var compact = Uri.UnescapeDataString(match.Groups["jwt"].Value);
        if (!TokenFixtures.LooksSigned(compact))
        {
            return (url, null);
        }

        var hint = TokenFixtures.Describe(
            Parameter, compact, jwks is null ? null : token => TokenFixtures.Verify(token, jwks));

        return (url.Remove(match.Groups["jwt"].Index, match.Groups["jwt"].Length)
                   .Insert(match.Groups["jwt"].Index, hint.Placeholder),
                hint);
    }

    /// <summary>
    /// Matches the hint's value.
    /// </summary>
    /// <remarks>
    /// Percent-encoding is allowed for, because a URL is recorded as it was sent rather than as it
    /// was assembled. A separator behind <c>%2E</c> is the same trap that once hid a token from a
    /// guard and from an ad-hoc probe, which then printed it.
    /// </remarks>
    [GeneratedRegex(@"[?&]id_token_hint=(?<jwt>[A-Za-z0-9_\-]+(?:\.|%2[Ee])[A-Za-z0-9_\-]+(?:\.|%2[Ee])[A-Za-z0-9_\-]+)")]
    private static partial Regex HintParameter();
}
