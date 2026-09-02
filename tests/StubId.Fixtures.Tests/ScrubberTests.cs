using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The guards protect the repository, so they get tested against what they are meant to
/// catch. A check that has never been seen to fail is not yet a check.
/// </summary>
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
        var original = Environment.GetEnvironmentVariable("STUBID_NEB_PP_CLIENT_ID");
        Environment.SetEnvironmentVariable("STUBID_NEB_PP_CLIENT_ID", "a-private-client/id");

        try
        {
            Assert.Equal(
                "client_id={{NEB_PP_CLIENT_ID}}",
                Scrubber.Scrub("client_id=a-private-client/id"));

            Assert.Equal(
                "ReturnUrl=%2Fop%3Fclient_id%3D{{NEB_PP_CLIENT_ID}}",
                Scrubber.Scrub("ReturnUrl=%2Fop%3Fclient_id%3Da-private-client%2Fid"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUBID_NEB_PP_CLIENT_ID", original);
        }
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

    [Fact]
    public void A_configured_credential_is_substituted_before_the_request_is_sent()
    {
        var sent = Scrubber.Unscrub(
            "client_secret={{NEB_PP_OPEN_CLIENT_CODE_SECRET}}", _ => "the-real-secret");

        Assert.Equal("client_secret=the-real-secret", sent);
    }
}
