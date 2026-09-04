using System.Net;

namespace StubId.Testing.Tests;

/// <summary>
/// The admin page answers wherever the instance does.
/// </summary>
/// <remarks>
/// Both listeners, deliberately, and this is the only test that can say so: the in-memory suites
/// have one transport and the in-process host has none. TLS on this instance adds a listener
/// rather than replacing one, so an operator who reaches an instance at all reaches its pages -
/// which is the arrangement the guide describes and therefore the one worth pinning.
/// </remarks>
[Trait("Category", "Container")]
public class AdminReachTests : IAsyncLifetime
{
    private StubIdContainer _stub = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _stub = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct)).WithTls().Build();
        await _stub.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task The_admin_page_answers_on_both_listeners()
    {
        using var trusting = _stub.CreateTrustingHandler();
        using var secured = new HttpClient(trusting, disposeHandler: false);
        using var plain = new HttpClient();

        var addresses = new[]
        {
            (Client: plain, Address: new Uri(_stub.MappedAddress, "_stubid/admin")),
            (Client: secured, Address: new Uri(_stub.BaseAddress, "_stubid/admin")),
        };

        foreach (var (client, address) in addresses)
        {
            using var page = await client.GetAsync(address, Ct);

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);

            // TRADEMARKS.md undertakes that no page of StubID's suggests a real authentication.
            Assert.Contains(
                "StubID is an emulator",
                await page.Content.ReadAsStringAsync(Ct),
                StringComparison.Ordinal);
        }
    }
}
