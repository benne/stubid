using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// The issuer is what this instance was told, never what the request asked for.
/// </summary>
/// <remarks>
/// A client library compares the issuer it discovers against the authority it was configured
/// with, character for character - openid-client and Spring Security both do, and neither
/// forgives a difference. An issuer built from the Host header is therefore right for whoever
/// asked and wrong for everybody else: a browser reaching a container on a mapped port and an
/// application reaching the same container by service name would discover two different issuers
/// from one instance. Refusing to answer is a bad afternoon; answering plausibly and wrongly is
/// a key-resolution error days later with nothing on the client's side to explain it.
/// </remarks>
public class PublicBaseUrlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_instance_that_was_never_told_its_address_refuses_rather_than_guessing()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/op/.well-known/openid-configuration", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(
            "the public base URL is not set",
            body.RootElement.GetProperty("error").GetString());
    }

    /// <remarks>
    /// The guard is at the point the address is read, not on the /op prefix, and this is what
    /// keeps that true. The key set does not depend on the address, and RestartTests fetches it
    /// without ever setting one - a path-prefix gate would answer 503 there and take the test
    /// that catches the project's worst failure mode with it.
    /// </remarks>
    [Fact]
    public async Task The_key_set_is_served_before_the_address_is_known()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration/jwks", Ct));

        Assert.NotEmpty(document.RootElement.GetProperty("keys").EnumerateArray());
    }

    /// <remarks>
    /// Discarding a bad value and carrying on would answer a later 503 telling the operator to
    /// set the key they did set. The process refuses to start instead, while they can still read
    /// why.
    /// </remarks>
    [Fact]
    public void Configuration_that_would_produce_a_wrong_issuer_stops_the_host_from_starting()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://host:8080/op"));

            factory.CreateClient();
        });

        Assert.Contains("/op", failure.Message, StringComparison.Ordinal);
    }
}
