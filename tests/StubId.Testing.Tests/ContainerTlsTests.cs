using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StubId.Wire;

namespace StubId.Testing.Tests;

/// <summary>
/// The container serves TLS, and a client library needs nothing relaxed to talk to it.
/// </summary>
/// <remarks>
/// This is the adoption claim, tested rather than asserted in a README: an application changes its
/// authority and nothing else. The .NET handler refuses a metadata address that is not https unless
/// RequireHttpsMetadata is turned off, and every guide that tells an integrator to turn it off is
/// teaching a habit that outlives the test it was written for.
/// <para>
/// Its own container rather than the shared one, because TLS is a property of how the instance was
/// started and the rest of the suite wants the plain one.
/// </para>
/// </remarks>
[Trait("Category", "Container")]
public class ContainerTlsTests : IAsyncLifetime
{
    private StubIdContainer _stub = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _stub = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct))
            .WithTls()
            .Build();

        await _stub.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public void The_authority_a_client_is_configured_with_is_an_https_one()
    {
        Assert.Equal(Uri.UriSchemeHttps, _stub.Authority.Scheme);
        Assert.EndsWith("/op", _stub.Authority.ToString(), StringComparison.Ordinal);

        // The control API stays on plain HTTP, so creating a citizen never waits on a trust decision.
        Assert.Equal(Uri.UriSchemeHttp, _stub.MappedAddress.Scheme);
    }

    [Fact]
    public async Task The_discovered_issuer_over_TLS_is_the_secured_address()
    {
        using var handler = _stub.CreateTrustingHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        using var document = JsonDocument.Parse(
            await client.GetStringAsync(
                new Uri(_stub.Authority + "/.well-known/openid-configuration"), Ct));

        Assert.Equal(
            _stub.Authority.ToString(),
            document.RootElement.GetProperty("issuer").GetString());
    }

    /// <remarks>
    /// The handler trusts one certificate rather than waving validation through, and this is the
    /// test that tells those two apart. Without it the trusting handler would be indistinguishable
    /// from the shortcut it exists to avoid - and that shortcut is one copied line away from a
    /// production client that validates nothing.
    /// </remarks>
    [Fact]
    public void The_handler_trusts_this_instances_certificate_and_no_other()
    {
        Assert.NotNull(_stub.ServerCertificate);

        using var handler = _stub.CreateTrustingHandler();
        var trusts = handler.ServerCertificateCustomValidationCallback;

        Assert.NotNull(trusts);
        Assert.True(trusts(null!, _stub.ServerCertificate, null, SslPolicyErrors.None));

        using var somebodyElse = CertificateFactory.CreateServerCertificate(
            "StubID", ["localhost"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Same subject, same name, different key. Nothing but the bytes distinguishes them.
        Assert.False(trusts(null!, somebodyElse, null, SslPolicyErrors.None));
        Assert.False(trusts(null!, null, null, SslPolicyErrors.None));
    }

    [Fact]
    public async Task A_login_completes_over_TLS()
    {
        const string CodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

        var citizen = await _stub.Citizens.CreateAsync(
            new Client.CitizenSpec { Name = "Karen Refsgaard", DateOfBirth = new DateOnly(1979, 11, 2) },
            Ct);

        await _stub.Behaviour.EnqueueAsync(
            Client.Decision.Approved(citizen.Id).ForClient(CodeClient), Ct);

        using var handler = _stub.CreateTrustingHandler();
        handler.AllowAutoRedirect = false;

        using var browser = new HttpClient(handler, disposeHandler: false);

        // Built from the authority rather than through a base address: the authority carries the
        // /op segment, and a request path beginning with a slash would discard it.
        using var authorize = await browser.GetAsync(
            new Uri(
                _stub.Authority
                + $"/connect/authorize?client_id={CodeClient}&response_type=code"
                + "&redirect_uri=http://localhost:5099/callback"
                + "&scope=openid%20mitid&state=s&nonce=n"),
            Ct);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

        var returned = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query);

        Assert.False(string.IsNullOrEmpty(returned["code"]));
        Assert.Equal(_stub.Authority.ToString(), returned["iss"]);
    }

    /// <remarks>
    /// The file a node process or a JVM is handed has to be the certificate this instance actually
    /// presents, or every recipe in the certificates guide trusts the wrong thing quietly. Fetched
    /// over plain HTTP, which is the only transport a caller can reach before it has been given the
    /// means to decide anything about trust - the same bootstrap the interop job uses.
    /// </remarks>
    [Fact]
    public async Task The_PEM_the_container_publishes_is_the_certificate_it_serves()
    {
        using var plain = new HttpClient { BaseAddress = _stub.MappedAddress };
        using var response = await plain.GetAsync("/_stubid/v1/runtime/tls-certificate.pem", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pem-certificate-chain", response.Content.Headers.ContentType?.MediaType);

        var pem = await response.Content.ReadAsStringAsync(Ct);

        // Exporting writes no trailing newline, so the route appends one. This is the only place
        // that guarantee can be checked against what is actually served, and it matters because
        // appending a second instance's certificate to this file is a thing people do.
        Assert.EndsWith("\n", pem, StringComparison.Ordinal);

        using var published = X509Certificate2.CreateFromPem(pem);

        Assert.Equal(_stub.ServerCertificate!.Thumbprint, published.Thumbprint);
    }
}

/// <summary>
/// TLS with a certificate the caller supplies rather than one the instance generates.
/// </summary>
/// <remarks>
/// The case where the certificate has to chain to something the environment already trusts, so that
/// nothing needs per-instance trust at all. Untested, this mode is a configuration path that has
/// never once been executed - and the failure it would produce is a container that starts and then
/// refuses every handshake.
/// </remarks>
[Trait("Category", "Container")]
public class ContainerSuppliedCertificateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_certificate_the_instance_serves_is_the_one_it_was_given()
    {
        using var mine = CertificateFactory.CreateServerCertificate(
            "A certificate of my own",
            ["localhost", "127.0.0.1"],
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        await using var stub = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct))
            .WithTlsCertificate(mine.Export(X509ContentType.Pkcs12, "supplied"), "supplied")
            .Build();

        await stub.StartAsync(Ct);

        Assert.Equal(mine.Thumbprint, stub.ServerCertificate?.Thumbprint);

        using var handler = stub.CreateTrustingHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        using var document = JsonDocument.Parse(
            await client.GetStringAsync(
                new Uri(stub.Authority + "/.well-known/openid-configuration"), Ct));

        Assert.Equal(
            stub.Authority.ToString(),
            document.RootElement.GetProperty("issuer").GetString());
    }
}
