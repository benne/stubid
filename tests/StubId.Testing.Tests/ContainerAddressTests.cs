using System.Text.Json;

namespace StubId.Testing.Tests;

/// <summary>
/// The container answers with the address the test reaches it at.
/// </summary>
/// <remarks>
/// This is what the whole runtime-address mechanism exists for, and the only place it is proved
/// against a real mapped port. Docker assigns that port when it starts the container, so the issuer
/// cannot be configured before the fact; a stub that answered with its own internal 8080 instead
/// would satisfy .NET's handler and fail openid-client and Spring Security, both of which compare
/// the issuer they discover against the authority they were configured with character for character.
/// </remarks>
[Trait("Category", "Container")]
[Collection(StubIdCollection.Name)]
public class ContainerAddressTests(StubIdInstance stub, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_discovered_issuer_is_the_address_the_test_reaches()
    {
        // Reported, not asserted on: a cold machine builds the image first, which is not a property
        // of this codebase and not something a test should fail over.
        output.WriteLine($"Container ready in {stub.StartupDuration.TotalSeconds:0.00} s.");

        using var client = new HttpClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync(
                new Uri(stub.Container.Authority + "/.well-known/openid-configuration"), Ct));

        Assert.Equal(
            stub.Container.Authority.ToString(),
            document.RootElement.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task Readiness_is_true_once_the_container_has_started()
    {
        Assert.True(await stub.Container.Control.IsReadyAsync(Ct));
        Assert.True(await stub.Container.Control.IsLiveAsync(Ct));

        Assert.Equal(
            stub.Container.BaseAddress.ToString().TrimEnd('/'),
            (await stub.Container.Control.Runtime.GetPublicBaseUrlAsync(Ct))?.ToString().TrimEnd('/'));
    }

    /// <remarks>
    /// The compose case: the browser and the application reach StubID by different names and both
    /// must see one issuer, so the caller pins it and the module has to leave it alone rather than
    /// overwrite it with the port this process happens to use.
    /// </remarks>
    [Fact]
    public async Task A_pinned_address_survives_the_handshake()
    {
        await using var pinned = new StubIdBuilder(await StubIdImage.ResolveAsync(Ct))
            .WithPublicBaseUrl(new Uri("http://stubid.example:8080"))
            .Build();

        await pinned.StartAsync(Ct);

        using var client = new HttpClient { BaseAddress = pinned.MappedAddress };
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration", Ct));

        Assert.Equal(
            "http://stubid.example:8080/op",
            document.RootElement.GetProperty("issuer").GetString());
    }
}
