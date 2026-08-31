using System.Net;
using System.Net.Http.Json;
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

    [Fact]
    public async Task The_issuer_follows_a_runtime_change_without_a_restart()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal("http://first.example:9999/op", await IssuerAfterSetting(client, "http://first.example:9999"));
        Assert.Equal("http://second.example/op", await IssuerAfterSetting(client, "http://second.example"));
    }

    [Fact]
    public async Task Configuration_seeds_the_address_and_a_later_call_replaces_it()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"));
        using var client = factory.CreateClient();

        using var seeded = JsonDocument.Parse(
            await client.GetStringAsync("/_stubid/v1/runtime/public-base-url", Ct));

        Assert.Equal("http://localhost", seeded.RootElement.GetProperty("publicBaseUrl").GetString());

        Assert.Equal(
            "http://localhost:18080/op",
            await IssuerAfterSetting(client, "http://localhost:18080"));
    }

    [Fact]
    public async Task Readiness_is_false_until_the_address_is_known()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using (var before = await client.GetAsync("/_stubid/health/ready", Ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        }

        using (var live = await client.GetAsync("/_stubid/health/live", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        await IssuerAfterSetting(client, "http://localhost:18080");

        using var after = await client.GetAsync("/_stubid/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:8080")]
    [InlineData("ftp://host")]
    [InlineData("http://host:8080/op")]
    [InlineData("http://host:8080/tenant")]
    [InlineData("http://host:8080?a=b")]
    public async Task An_address_that_would_produce_a_wrong_issuer_is_refused(string candidate)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/_stubid/v1/runtime/public-base-url", new { publicBaseUrl = candidate }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var error = body.RootElement.GetProperty("error").GetString();

        Assert.False(string.IsNullOrWhiteSpace(error));

        // The one an operator actually hits, by pasting the authority out of their client
        // configuration. It has to say which segment is the problem, not just that one is.
        if (candidate.EndsWith("/op", StringComparison.Ordinal))
        {
            Assert.Contains("/op", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_trailing_slash_is_removed_so_the_issuer_never_carries_two()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal("http://host:8080/op", await IssuerAfterSetting(client, "http://host:8080/"));
    }

    /// <remarks>
    /// Discovery is a substitution and the tokens are string concatenation, so proving one says
    /// nothing about the other. This drives an authorize request to its redirect and reads the
    /// iss parameter the broker puts on it, which openid-client refuses a response without.
    /// </remarks>
    [Fact]
    public async Task A_front_channel_response_carries_the_issuer_that_was_set_last()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await IssuerAfterSetting(client, "http://front.example:7000");

        using var response = await client.GetAsync(
            "/op/connect/authorize"
            + "?client_id=0a775a87-878c-4b83-abe3-ee29c720c3e7"
            + "&response_type=code"
            + "&redirect_uri=http://localhost:5099/callback"
            + "&scope=openid%20mitid&state=s&nonce=n",
            Ct);

        var location = response.Headers.Location?.ToString() ?? "";
        var returned = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);

        Assert.Equal("http://front.example:7000/op", returned["iss"]);
    }

    /// <remarks>
    /// Reset is protocol state, and the address is not protocol state. A suite that reuses one
    /// container resets between tests; if that dropped the address, every issuer after the first
    /// reset would be wrong, which is the failure this whole file exists to prevent arriving by
    /// the back door.
    /// </remarks>
    [Fact]
    public async Task A_reset_does_not_make_the_instance_forget_its_address()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        await IssuerAfterSetting(client, "http://kept.example:8080");

        using (var reset = await client.PostAsync("/_stubid/v1/reset", content: null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration", Ct));

        Assert.Equal(
            "http://kept.example:8080/op",
            document.RootElement.GetProperty("issuer").GetString());
    }

    private static async Task<string?> IssuerAfterSetting(HttpClient client, string address)
    {
        using var set = await client.PutAsJsonAsync(
            "/_stubid/v1/runtime/public-base-url", new { publicBaseUrl = address }, Ct);

        set.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration", Ct));

        return document.RootElement.GetProperty("issuer").GetString();
    }
}
