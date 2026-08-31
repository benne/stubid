using StubId.Client;

namespace StubId.InProcess.Tests;

/// <summary>Two instances in one process, which is a thing only an in-process host can be asked for.</summary>
/// <remarks>
/// Worth proving rather than assuming. Every piece of state the emulator keeps is a singleton in
/// its own container, so two instances share nothing in memory - but they do share one key
/// directory on disk by default, and they start at the same time here on purpose so that the race
/// KeyRaceTests covers is actually run rather than described.
/// </remarks>
public class MultipleHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_instances_in_one_process_keep_their_own_citizens()
    {
        await using var first = new StubIdHostBuilder().Build();
        await using var second = new StubIdHostBuilder().Build();

        await Task.WhenAll(first.StartAsync(Ct), second.StartAsync(Ct));

        var citizen = await first.Citizens.CreateAsync(
            new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) },
            Ct);

        Assert.Contains(await first.Citizens.ListAsync(Ct), c => c.Id == citizen.Id);
        Assert.DoesNotContain(await second.Citizens.ListAsync(Ct), c => c.Id == citizen.Id);
    }

    /// <remarks>
    /// Also the check that the served discovery document is genuinely rewritten. The default
    /// address is chosen so that substitution is never proven by an identity replace, and pinning
    /// a second, different address here is what makes that claim hold for two values rather than
    /// for one.
    /// </remarks>
    [Fact]
    public async Task Two_instances_carry_the_issuers_they_were_each_given()
    {
        await using var first = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid-first.invalid"))
            .Build();

        await using var second = new StubIdHostBuilder()
            .WithPublicBaseUrl(new Uri("https://stubid-second.invalid"))
            .Build();

        await Task.WhenAll(first.StartAsync(Ct), second.StartAsync(Ct));

        Assert.Equal("https://stubid-first.invalid/op", await IssuerOf(first));
        Assert.Equal("https://stubid-second.invalid/op", await IssuerOf(second));
    }

    private static async Task<string?> IssuerOf(StubIdHost stub)
    {
        using var client = stub.CreateClient();
        using var document = System.Text.Json.JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration", Ct));

        return document.RootElement.GetProperty("issuer").GetString();
    }
}
