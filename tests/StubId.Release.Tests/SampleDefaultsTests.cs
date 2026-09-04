using System.Text.Json;
using StubId.Testing;

namespace StubId.Release.Tests;

/// <summary>
/// The settings the sample falls back to when nobody tells it anything.
/// </summary>
/// <remarks>
/// Everything that drives the sample in CI overrides both addresses, because the container's ports
/// are mapped and cannot be known in advance. That leaves the committed defaults - the values that
/// decide whether a reader's bare <c>dotnet run</c> works - executed by nothing at all. Someone
/// moving one of them to 8444 would leave the guide broken and every test green.
/// <para>
/// So they are pinned here against the ports the Testcontainers module names, which is a different
/// artefact arriving at the same two numbers rather than this file agreeing with itself.
/// </para>
/// </remarks>
public class SampleDefaultsTests
{
    private static JsonElement StubIdSection()
    {
        var path = Path.Combine(Repository.Root, "samples", "aspnetcore", "appsettings.json");

        Assert.True(File.Exists(path), $"{path} does not exist.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("StubId").Clone();
    }

    /// <summary>The authority is the secured port, which is where the issuer points.</summary>
    [Fact]
    public void The_samples_default_authority_is_the_port_the_image_serves_TLS_on()
    {
        var authority = new Uri(StubIdSection().GetProperty("Authority").GetString()!);

        Assert.Equal(Uri.UriSchemeHttps, authority.Scheme);
        Assert.Equal(StubIdBuilder.StubIdTlsPort, authority.Port);
        Assert.Equal("/op", authority.AbsolutePath);
    }

    /// <summary>
    /// The control port is the plain one, and it has to stay plain.
    /// </summary>
    /// <remarks>
    /// This is not a stylistic preference. The sample reads the certificate through this address
    /// before it has any basis for trusting anything, so an https control port would be a bootstrap
    /// that cannot start.
    /// </remarks>
    [Fact]
    public void The_samples_default_control_address_is_the_plain_port()
    {
        var control = new Uri(StubIdSection().GetProperty("ControlUrl").GetString()!);

        Assert.Equal(Uri.UriSchemeHttp, control.Scheme);
        Assert.Equal(StubIdBuilder.StubIdPort, control.Port);
    }

    /// <summary>
    /// The sample names a client the broker state actually registers.
    /// </summary>
    /// <remarks>
    /// There are three, they are fixed, and an unregistered one is refused outright - so a typo
    /// here is a sample that cannot sign in, discovered by the reader rather than by CI.
    /// </remarks>
    [Fact]
    public void The_samples_default_client_is_one_the_broker_knows()
    {
        var clientId = StubIdSection().GetProperty("ClientId").GetString()!;

        Assert.True(
            new StubId.Server.BrokerState().Allows(clientId, "code"),
            $"{clientId} is not a registered client that may ask for a code.");
    }
}
