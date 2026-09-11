using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The guards protect the repository, so they get tested against what they are meant to
/// catch. A check that has never been seen to fail is not yet a check.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public class ScrubberTests
{
    [Theory]
    // Obviously fake on purpose. The guard checks shape, so the sample does not need to be
    // a real credential, and a real one here would trip the secret scanner that backs it up.
    [InlineData("client_secret=NOT%2FA%2FREAL%2FSECRET%2Fvalue1234567890")]
    [InlineData("client_secret=aVeryLongLookingSecretValue123456")]
    [InlineData("password=hunter2hunter2hunter2")]
    [InlineData("""{"assertion":"aVeryLongLookingSecretValue123456"}""")]
    public void An_unscrubbed_credential_is_caught(string body)
    {
        Assert.True(Scrubber.FindUnscrubbedCredential(body).Success);
    }

    [Theory]
    [InlineData("client_secret=%7B%7BNEB_PP_OPEN_CLIENT_CODE_SECRET%7D%7D")]
    [InlineData("client_secret={{NEB_PP_OPEN_CLIENT_CODE_SECRET}}")]
    [InlineData("client_secret=wrong-secret")]
    [InlineData("code=not-a-real-code&client_id=0a775a87-878c-4b83-abe3-ee29c720c3e7")]
    public void A_placeholder_or_a_deliberately_useless_value_is_not(string body)
    {
        Assert.False(Scrubber.FindUnscrubbedCredential(body).Success);
    }

    [Theory]
    // Serials ending 9995 are Denmark's published test numbers, deliberately allocated last.
    [InlineData(@"{""cpr"":""0101709995""}")]
    [InlineData("cpr=3112899995")]
    [InlineData(@"{""cpr"":""311289-9995""}")]                              // the separated form
    [InlineData("eyJzdWIiOiJ4IiwiZGsuY3ByIjoiMDEwMTcwOTk5NSJ9")]          // hidden inside a token
    public void Something_shaped_like_a_cpr_number_is_caught(string text)
    {
        Assert.True(SensitiveContent.FindCpr(text).Found);
    }

    [Theory]
    // A header beginning with alg, and one beginning with typ. The second is what the old
    // literal check missed. The transaction token was the unobserved token that argued for
    // checking structurally rather than by prefix; it turned out to be alg-first like the
    // rest, which is a reason to keep the structural check and not a reason to drop it.
    [InlineData("eyJhbGciOiJSUzI1NiIsImtpZCI6IlgifQ.eyJzdWIiOiJhLXN1YmplY3QifQ.c2ln")]
    [InlineData("eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhLXN1YmplY3QifQ.c2ln")]
    public void A_signed_token_is_caught_whatever_its_header_order(string text)
    {
        Assert.True(SensitiveContent.FindSignedToken(text).Found);
    }

    [Theory]
    [InlineData("just some ordinary text with a long-word-that-is-not-a-token")]
    [InlineData("048058BB59F4D3007045896FD488CE81F4EB4923.7FF447FA0FB65A7E749E8B43AC635862.x")]
    public void Something_that_merely_looks_token_shaped_is_not(string text)
    {
        Assert.False(SensitiveContent.FindSignedToken(text).Found);
    }

    [Theory]
    [InlineData("048058BB59F4D3007045896FD488CE81F4EB4923")] // a certificate thumbprint
    [InlineData(@"{""cpr"":""6101709995""}")]                // a replacement number, day 61
    [InlineData("1234")]
    [InlineData(@"{""cpr"":""3102851234""}")]                // the 31st of February
    public void Digits_inside_a_longer_run_or_a_replacement_number_are_not(string text)
    {
        Assert.False(SensitiveContent.FindCpr(text).Found);
    }

    [Fact]
    public void A_configured_credential_is_replaced_wherever_it_is_echoed()
    {
        // The broker echoes the client_id back in the login redirect, so recording with a
        // private client would publish it unless responses are scrubbed too. Both the plain
        // and the percent-encoded form appear in practice.
        ProcessEnvironment.With(
            () =>
            {
                Assert.Equal(
                    "client_id={{NEB_PP_CLIENT_ID}}",
                    Scrubber.Scrub("client_id=a-private-client/id"));

                Assert.Equal(
                    "ReturnUrl=%2Fop%3Fclient_id%3D{{NEB_PP_CLIENT_ID}}",
                    Scrubber.Scrub("ReturnUrl=%2Fop%3Fclient_id%3Da-private-client%2Fid"));
            },
            ("STUBID_NEB_PP_CLIENT_ID", "a-private-client/id"));
    }

    [Fact]
    public void Nothing_is_replaced_when_nothing_is_configured()
    {
        // Recording with the published open client must leave the committed fixtures alone.
        const string text = """{"keys":[{"kid":"7FF447FA0FB65A7E749E8B43AC635862381F0CC3"}]}""";

        Assert.Equal(text, Scrubber.Scrub(text));
    }

    [Fact]
    public void Recording_refuses_to_run_without_the_credential_rather_than_sending_a_placeholder()
    {
        // Sending the placeholder would record a confusing refusal in place of the exchange
        // the case exists to capture.
        //
        // The resolver is passed in rather than read from the machine. This test previously
        // cleared an environment variable and passed only while the local configuration file
        // happened not to carry the setting: green on a fresh checkout, red once someone
        // configured their machine to actually record.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => Scrubber.Unscrub("client_secret={{NEB_PP_OPEN_CLIENT_CODE_SECRET}}", _ => null));

        Assert.Contains("STUBID_NEB_PP_CODE_CLIENT_SECRET", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A name inside a base64 claim value is scrubbed like one written plainly.</summary>
    /// <remarks>
    /// The broker sends the CPR consent sentence base64-encoded, and it names the organization
    /// receiving the number. The redact block has always carried an entry for that name and it
    /// never matched, because base64 encodes three bytes at a time and the same name at a
    /// different offset is a different substring - the rule and the value it was written for sat
    /// one encoding apart.
    /// </remarks>
    [Fact]
    public void A_name_inside_a_base64_claim_is_scrubbed_too()
    {
        var sentence = "Ved at indtaste det her, godkender du, at Example A/S modtager dit CPR-nummer.";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sentence));

        var scrubbed = Scrubber.Scrub(
            $$"""{"mitid.cpr_consent_text":"{{encoded}}"}""",
            setting => null,
            [("{{ORGANIZATION_NAME}}", "Example A/S")]);

        Assert.DoesNotContain("Example A/S", Decode(scrubbed), StringComparison.Ordinal);
        Assert.Contains("{{ORGANIZATION_NAME}}", Decode(scrubbed), StringComparison.Ordinal);

        // The sentence around it survives: what is redacted is the name, not the recording.
        Assert.Contains("modtager dit CPR-nummer.", Decode(scrubbed), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two claims that describe the client are blanked with nothing configured.
    /// </summary>
    /// <remarks>
    /// The resolver returns nothing and the redact list is empty on purpose: these are the only
    /// redactions that hold on a machine nobody has configured. Both values are unknowable in
    /// advance - a router hands out one, the broker computes the other per sitting - so a rule
    /// keyed on the value would be right once and silent afterwards.
    /// </remarks>
    [Theory]
    [InlineData("""{"transaction_client_ip":"198.51.100.7"}""", "{{CLIENT_IP}}", "198.51.100.7")]
    [InlineData("""{"mitid.geo_ip_distance_km":"8396"}""", "{{GEO_IP_DISTANCE_KM}}", "8396")]
    // Unquoted, in case a broker ever stops sending the distance as a string.
    [InlineData("""{"mitid.geo_ip_distance_km":8396}""", "{{GEO_IP_DISTANCE_KM}}", "8396")]
    public void A_claim_describing_the_client_is_blanked_with_nothing_configured(
        string body, string placeholder, string original)
    {
        var scrubbed = Scrubber.Scrub(body, _ => null, []);

        Assert.Contains(placeholder, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(original, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>A signed token is not rewritten on its way past.</summary>
    /// <remarks>
    /// The claim lives inside the transaction token too, and that token is signed. Decoding it to
    /// blank a claim would leave a signature over bytes that are no longer there - a recording
    /// that cannot be verified is worse than one that carries a value. The signed form is replaced
    /// whole, elsewhere; this only has to leave it alone.
    /// </remarks>
    [Fact]
    public void A_signed_token_carrying_the_claim_is_left_alone()
    {
        // Assembled rather than written out. A token long enough to carry this claim is also long
        // enough to read as a credential to a secret scanner, and the scanner is right to say so -
        // a sample that has to be allowlisted somewhere is a worse sample than one that is built.
        var token = string.Join('.',
            Segment("""{"alg":"RS256"}"""),
            Segment("""{"transaction_client_ip":"198.51.100.7"}"""),
            "c2ln");

        Assert.Equal(token, Scrubber.Scrub(token, _ => null, []));
    }

    /// <summary>One base64url segment of a compact token, as a signer would write it.</summary>
    private static string Segment(string json) =>
        System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>Base64 that is not readable text is left exactly as it was.</summary>
    /// <remarks>
    /// A signature, a hash and a key are all base64 and none of them is a sentence. Decoding and
    /// re-encoding one would change bytes the recording exists to preserve.
    /// </remarks>
    [Fact]
    public void Base64_that_is_not_text_is_left_alone()
    {
        const string text = """{"mitid.transaction_text_sha256":"KJjd1jCP2c+GnkLNl8cAEquyjsxVf6FI6EtoVddhHR8="}""";

        Assert.Equal(text, Scrubber.Scrub(text, _ => null, [("{{ORGANIZATION_NAME}}", "Example A/S")]));
    }

    private static string Decode(string json)
    {
        var value = json.Split('"')[3];

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    [Fact]
    public void A_configured_credential_is_substituted_before_the_request_is_sent()
    {
        var sent = Scrubber.Unscrub(
            "client_secret={{NEB_PP_OPEN_CLIENT_CODE_SECRET}}", _ => "the-real-secret");

        Assert.Equal("client_secret=the-real-secret", sent);
    }

    /// <summary>
    /// A hostname that names somebody's tenant, which is the second broker's version of a
    /// problem the first one could not have.
    /// </summary>
    [Theory]
    [InlineData("https://an-account.sandbox.signicat.com/auth/open")]
    [InlineData("""{"iss":"https://an-account.sandbox.signicat.com/auth/open"}""")]
    // A hostname does not care about case, and neither does this.
    [InlineData("https://An-Account.Sandbox.Signicat.com/auth/open")]
    // A single-label account, which is what most of them are.
    [InlineData("https://acme.sandbox.signicat.com/auth/open/.well-known/openid-configuration")]
    public void A_tenant_host_is_caught(string text)
    {
        Assert.True(SensitiveContent.FindTenantHost(text).Found);
    }

    /// <summary>
    /// The forms documentation actually uses, and the platform's shared hosts.
    /// </summary>
    /// <remarks>
    /// This is the half that decides whether the guard is usable. A check that made it
    /// impossible to write the URL down would be worked around within a week, so both ways of
    /// writing it - the placeholder the scrubber substitutes, and the angle-bracketed form a
    /// research note uses - have to pass without anyone adding an exemption.
    /// </remarks>
    [Theory]
    [InlineData("https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com/auth/open")]
    [InlineData("https://<subdomain>.sandbox.signicat.com/auth/open")]
    // Shared infrastructure rather than a tenant: everyone's redirect chain ends here.
    [InlineData("https://preprod.signicat.com/std/method/dtp")]
    [InlineData("https://signicat.pp.mitid.dk/")]
    public void A_placeholder_or_a_shared_host_is_not(string text)
    {
        Assert.False(SensitiveContent.FindTenantHost(text).Found);
    }

    /// <summary>A tenant host inside a token is found like one written out.</summary>
    /// <remarks>
    /// This is where it actually lives. Every token a sitting produces carries the issuer in
    /// <c>iss</c>, and a base64url segment is one long alphanumeric run that no amount of
    /// reading the file would reveal.
    /// </remarks>
    [Fact]
    public void A_tenant_host_inside_a_token_is_caught()
    {
        var token = string.Join('.',
            Segment("""{"alg":"RS256"}"""),
            Segment("""{"iss":"https://an-account.sandbox.signicat.com/auth/open"}"""),
            "c2ln");

        var finding = SensitiveContent.FindTenantHost(token);

        Assert.True(finding.Found);
        Assert.Contains("base64url", finding.Location, StringComparison.Ordinal);
    }

    /// <summary>
    /// Something with no shape at all, found because this machine was told the value.
    /// </summary>
    /// <remarks>
    /// The finding names the setting rather than the value, which is what makes the message
    /// safe to print. A failure is read in a terminal, pasted into an issue and kept in a build
    /// log, and none of those is a place to put the thing the check exists to keep out of a file.
    /// </remarks>
    [Fact]
    public void A_configured_value_is_found_and_reported_by_name()
    {
        var finding = SensitiveContent.FindConfigured(
            "the client is sandbox-nnnnnnnnnnnnnnnnnnnn and it works",
            [("STUBID_SIGNICAT_CLIENT_ID", "sandbox-nnnnnnnnnnnnnnnnnnnn")]);

        Assert.True(finding.Found);
        Assert.Equal("STUBID_SIGNICAT_CLIENT_ID", finding.Value);
    }

    /// <summary>The escaped form too, because a URL carries values that way.</summary>
    /// <remarks>
    /// The same gap that put a real client secret in the very first fixture: the scrubber ran
    /// after the form had been percent-encoded, and by then the value was a different string.
    /// </remarks>
    [Fact]
    public void A_configured_value_is_found_in_the_form_a_url_carries_it_in()
    {
        var finding = SensitiveContent.FindConfigured(
            "recipient=Example%20A%2FS&amount=100",
            [("{{ORGANIZATION_NAME}}", "Example A/S")]);

        Assert.True(finding.Found);
        Assert.Equal("{{ORGANIZATION_NAME}}", finding.Value);
    }

    /// <summary>Inside a token as well, for the same reason as everything else here.</summary>
    [Fact]
    public void A_configured_value_inside_a_token_is_found_too()
    {
        var token = string.Join('.',
            Segment("""{"alg":"RS256"}"""),
            Segment("""{"iss":"https://an-account.sandbox.signicat.com/auth/open"}"""),
            "c2ln");

        var finding = SensitiveContent.FindConfigured(
            token, [("STUBID_SIGNICAT_DOMAIN", "an-account")]);

        Assert.True(finding.Found);
        Assert.Equal("STUBID_SIGNICAT_DOMAIN", finding.Value);
    }

    /// <summary>
    /// A value too short to search a repository for is not searched for.
    /// </summary>
    /// <remarks>
    /// It is still scrubbed - a replace does not care how long the string is. What it cannot
    /// survive is being matched as a substring against every file in the tree, where a short
    /// word is in half of them. A guard that cries wolf gets switched off, which costs more
    /// than the one file it was going to catch, so the floor is stated and
    /// <see cref="Preflight" /> reports anything under it rather than dropping it quietly.
    /// </remarks>
    [Fact]
    public void A_value_too_short_to_search_for_is_left_to_the_scrubber()
    {
        var finding = SensitiveContent.FindConfigured(
            "the acme corporation, of acme street, in acme",
            [("STUBID_SIGNICAT_DOMAIN", "acme")]);

        Assert.False(finding.Found);
    }

    /// <summary>
    /// The guard looks for exactly what the scrubber replaces, from one list.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Credentials that do not resolve are not searched for, because there
    /// is nothing to search for; configured redactions are, because a value worth replacing in
    /// a recording is worth finding in a document somebody typed.
    /// </remarks>
    [Fact]
    public void The_scan_looks_for_what_the_scrubber_replaces()
    {
        var configured = Scrubber.Configured(
            name => name == "STUBID_SIGNICAT_DOMAIN" ? "an-account" : null,
            [("{{ORGANIZATION_NAME}}", "Example A/S")]).ToList();

        Assert.Equal(
            [("STUBID_SIGNICAT_DOMAIN", "an-account"), ("{{ORGANIZATION_NAME}}", "Example A/S")],
            configured);
    }
}
