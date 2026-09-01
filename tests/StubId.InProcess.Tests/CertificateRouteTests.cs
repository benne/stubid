using System.Net;
using System.Text.Json;

namespace StubId.InProcess.Tests;

/// <summary>
/// An instance serving no TLS has no certificate to hand out, and says so rather than handing out
/// nothing.
/// </summary>
/// <remarks>
/// The TLS-off half of the certificate route, and this is the cheapest honest place for it: an
/// in-process host is definitionally TLS-off - StubId:Tls is pinned empty and WithSetting refuses
/// the key - so the branch runs here with no container, on Linux and on Windows, in milliseconds.
/// <para>
/// What it protects is the person who forgot StubId__Tls. They run the curl from the guide; a 200
/// with an empty body would leave them a file that is not a certificate, and the first thing that
/// tells them so is a handshake failure one command later.
/// </para>
/// </remarks>
public class CertificateRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_instance_with_no_TLS_refuses_the_certificate_rather_than_serving_nothing()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        using var client = stub.CreateClient();
        using var response = await client.GetAsync("/_stubid/v1/runtime/tls-certificate.pem", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()),
            "The refusal carried no error.");

        // The detail has to name the setting, because the reader is at a terminal with a curl that
        // just failed and nothing else to go on.
        Assert.Contains(
            "StubId:Tls",
            body.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The JSON route keeps answering, and keeps answering null. The two routes differ on purpose:
    /// that one is a question about a certificate, to which "there is none" is an answer, and this
    /// module's own host is the clearest case of it.
    /// </remarks>
    [Fact]
    public async Task The_route_that_asks_whether_there_is_one_still_answers()
    {
        await using var stub = new StubIdHostBuilder().Build();
        await stub.StartAsync(Ct);

        Assert.Null(await stub.Control.Runtime.GetTlsCertificateAsync(Ct));
    }
}
