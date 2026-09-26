using System.Text;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The token end session carries, and the promise that none of it reaches a fixture.
/// </summary>
/// <remarks>
/// The step that records a logout is the only one that puts the broker's own token in a request
/// URL. Nothing stripped it until this existed, which is why the second broker's sitting had no
/// end-session step at all.
/// </remarks>
public class IdTokenHintTests
{
    private const string Authority = "https://example.invalid/auth/open";

    private static string Jws(string header, string payload)
    {
        static string Segment(string json) =>
            System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        return $"{Segment(header)}.{Segment(payload)}.c2lnbmF0dXJlLWJ5dGVz";
    }

    private static string Token() =>
        Jws("""{"alg":"RS256","kid":"ABC"}""", """{"sub":"a-subject","sid":"a-session"}""");

    private static string Logout(string token) =>
        $"{Authority}/connect/endsession?id_token_hint={Uri.EscapeDataString(token)}"
        + "&post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A5099%2Fcallback&state=logout";

    [Fact]
    public void A_recorded_logout_leaves_no_token_in_the_url()
    {
        var url = Logout(Token());
        Assert.True(SensitiveContent.FindSignedToken(url).Found, "the sample is not a token");

        var (stripped, hint) = IdTokenHint.StripFrom(url);

        Assert.False(SensitiveContent.FindSignedToken(stripped).Found);
        Assert.Contains("{{ID_TOKEN_HINT}}", stripped, StringComparison.Ordinal);
        Assert.NotNull(hint);
        Assert.Equal("RS256", hint.Algorithm);
        Assert.Equal("ABC", hint.Kid);
        Assert.Contains("\"sid\"", hint.Payload, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The rest of the query is what the step exists to settle - whether the broker honors a
    /// post-logout address and echoes state - so the strip must not disturb it.
    /// </remarks>
    [Fact]
    public void Everything_beside_the_hint_survives()
    {
        var (stripped, _) = IdTokenHint.StripFrom(Logout(Token()));

        Assert.Contains("post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A5099%2Fcallback",
            stripped, StringComparison.Ordinal);
        Assert.Contains("state=logout", stripped, StringComparison.Ordinal);
        Assert.StartsWith($"{Authority}/connect/endsession?", stripped, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A URL is recorded as it was sent. A separator behind <c>%2E</c> is the trap that once hid
    /// a token from a guard and from a probe, which then printed it.
    /// </remarks>
    [Fact]
    public void A_hint_whose_separators_are_encoded_is_still_found()
    {
        var url = $"{Authority}/connect/endsession?id_token_hint="
            + Token().Replace(".", "%2E", StringComparison.Ordinal);

        var (stripped, hint) = IdTokenHint.StripFrom(url);

        Assert.NotNull(hint);
        Assert.False(SensitiveContent.FindSignedToken(stripped).Found);
    }

    [Fact]
    public void A_logout_carrying_no_hint_is_untouched()
    {
        const string url = Authority + "/connect/endsession";

        var (stripped, hint) = IdTokenHint.StripFrom(url);

        Assert.Equal(url, stripped);
        Assert.Null(hint);
    }

    /// <summary>
    /// The hint is the broker's token, so it is checked - and an unresolved kid is unchecked
    /// rather than failed.
    /// </summary>
    /// <remarks>
    /// The second broker answers with a different subset of its keys on every request, so a kid
    /// missing from the set the sitting fetched says nothing about the signature. Recording that
    /// as false would be a finding nobody made.
    /// </remarks>
    [Fact]
    public void A_signature_with_no_key_to_check_it_against_is_unchecked_not_failed()
    {
        var (_, withoutKeys) = IdTokenHint.StripFrom(Logout(Token()));
        Assert.Null(withoutKeys!.SignatureVerified);

        var (_, unknownKid) = IdTokenHint.StripFrom(
            Logout(Token()), """{"keys":[{"kty":"RSA","kid":"SOMETHING-ELSE","e":"AQAB","n":"bm90"}]}""");

        Assert.Null(unknownKid!.SignatureVerified);
    }
}
