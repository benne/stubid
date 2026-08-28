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
    [InlineData(@"{""cpr"":""0101709995""}")]
    [InlineData("cpr=3112894321")]
    public void Something_shaped_like_a_cpr_number_is_caught(string text)
    {
        Assert.True(Scrubber.FindCprShapedText(text).Success);
    }

    [Theory]
    [InlineData("048058BB59F4D3007045896FD488CE81F4EB4923")] // a certificate thumbprint
    [InlineData(@"{""cpr"":""6101709995""}")]                // a replacement number, day 61
    [InlineData("1234")]
    public void Digits_inside_a_longer_run_or_a_replacement_number_are_not(string text)
    {
        Assert.False(Scrubber.FindCprShapedText(text).Success);
    }

    [Fact]
    public void Recording_refuses_to_run_without_the_credential_rather_than_sending_a_placeholder()
    {
        // Sending the placeholder would record a confusing 400 in place of the exchange the
        // case exists to capture.
        var original = Environment.GetEnvironmentVariable("STUBID_NEB_PP_CODE_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("STUBID_NEB_PP_CODE_CLIENT_SECRET", null);

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(
                () => Scrubber.Unscrub("client_secret={{NEB_PP_OPEN_CLIENT_CODE_SECRET}}"));
            Assert.Contains("STUBID_NEB_PP_CODE_CLIENT_SECRET", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUBID_NEB_PP_CODE_CLIENT_SECRET", original);
        }
    }
}
