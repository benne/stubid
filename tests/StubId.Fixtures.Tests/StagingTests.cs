using System.Text;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

public class TokenFixtureTests
{
    private static string Jws(string header, string payload)
    {
        static string Segment(string json) =>
            System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        return $"{Segment(header)}.{Segment(payload)}.c2lnbmF0dXJl";
    }

    [Fact]
    public void A_token_is_replaced_by_a_placeholder_and_the_rest_of_the_document_is_untouched()
    {
        // The surrounding response keeps its member order, spacing and member positions,
        // which is what a recording of a token response is for. Scrubbing inside the token
        // would invalidate its signature, and re-signing would produce bytes the broker never
        // sent.
        var token = Jws("""{"alg":"RS256","kid":"ABC"}""", """{"sub":"a-subject"}""");
        var body = $$"""{"id_token":"{{token}}","token_type":"Bearer","expires_in":10800}""";

        var (rewritten, tokens) = TokenFixtures.Extract(body);

        Assert.Equal("""{"id_token":"{{ID_TOKEN}}","token_type":"Bearer","expires_in":10800}""", rewritten);
        Assert.Equal("RS256", tokens["id_token"].Algorithm);
        Assert.Equal("ABC", tokens["id_token"].Kid);
    }

    [Fact]
    public void The_decoded_halves_are_kept_verbatim()
    {
        // Member order inside the token is the entire evidence for what the broker sends, so
        // the decoded bytes are stored as they were rather than parsed and written back out.
        const string header = """{"typ":"JWT","alg":"RS256"}""";
        const string payload = """{"iss":"https://example","nbf":1,"sub":"x"}""";

        var (_, tokens) = TokenFixtures.Extract($$"""{"id_token":"{{Jws(header, payload)}}"}""");

        Assert.Equal(header, tokens["id_token"].Header);
        Assert.Equal(payload, tokens["id_token"].Payload);
    }

    [Fact]
    public void The_real_segment_lengths_survive_the_placeholder()
    {
        var token = Jws("""{"alg":"RS256"}""", """{"sub":"x"}""");
        var (_, tokens) = TokenFixtures.Extract($$"""{"id_token":"{{token}}"}""");

        Assert.Equal(token.Split('.').Select(p => p.Length), tokens["id_token"].SegmentLengths);
    }

    [Fact]
    public void A_body_that_is_not_json_is_returned_unchanged()
    {
        const string html = "<html><body>not json</body></html>";

        var (rewritten, tokens) = TokenFixtures.Extract(html);

        Assert.Equal(html, rewritten);
        Assert.Empty(tokens);
    }
}

public class StagingTests
{
    [Fact]
    public void The_same_value_always_becomes_the_same_placeholder()
    {
        // Whether the session identifier in one token equals the one in another is a question
        // the sitting pays an authentication to answer. A fresh pseudonym per occurrence would
        // destroy the answer while looking careful.
        var staging = new Staging();

        var first = staging.Discover("SID", "a-session-identifier");
        var again = staging.Discover("SID", "a-session-identifier");
        var other = staging.Discover("SID", "a-different-identifier");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void A_discovered_value_is_replaced_wherever_it_appears_including_encoded()
    {
        var staging = new Staging();
        staging.Discover("CODE", "the/authorization/code");

        Assert.Equal("code={{CODE_1}}", staging.Scrub("code=the/authorization/code"));
        Assert.Equal("code={{CODE_1}}", staging.Scrub("code=the%2Fauthorization%2Fcode"));
    }

    [Fact]
    public void Values_are_replaced_longest_first()
    {
        // Otherwise a shorter value that is a substring of a longer one rewrites part of it
        // and leaves the remainder exposed.
        var staging = new Staging();
        staging.Discover("TOKEN", "abcdefgh-suffix");
        staging.Discover("TOKEN", "abcdefgh");

        var scrubbed = staging.Scrub("value=abcdefgh-suffix");

        Assert.DoesNotContain("abcdefgh", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_reads_the_values_worth_hiding_out_of_a_token_response()
    {
        var staging = new Staging();
        staging.DiscoverIn("""{"access_token":"an-access-token-value","token_type":"Bearer"}""");

        Assert.DoesNotContain(
            "an-access-token-value",
            staging.Scrub("""{"access_token":"an-access-token-value"}"""),
            StringComparison.Ordinal);
    }
}

public class RedactionParsingTests
{
    [Fact]
    public void A_comment_is_not_a_redaction_rule()
    {
        // The example file carries its guidance under "//" keys. Treating one as a rule made
        // the scrubber replace the comment's own text wherever it appeared, and put a bogus
        // entry in every preflight report.
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "//": "Anything else that must not reach a fixture.",
              "{{ORGANISATION_CVR}}": "12345678"
            }
            """);

        var rules = StubId.CaptureHarness.LocalSettings.ParseRedactions(document.RootElement);

        Assert.Equal(["{{ORGANISATION_CVR}}"], rules.Keys);
    }
}
