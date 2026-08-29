using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Keys survive a restart.
/// </summary>
/// <remarks>
/// This is the failure every adopter meets first, and the one with nothing on their side to
/// explain it. Clients cache discovery metadata for twelve hours, so a server that generates
/// fresh keys on each start hands every integrating application a token signed by a key its
/// cached key set does not contain. What they see is IDX10501 and a working configuration
/// that stopped working.
/// </remarks>
public class RestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_key_set_is_the_same_after_a_restart()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"stubid-restart-{Guid.NewGuid():N}");

        try
        {
            var before = await KeyIdentifiers(keyPath);
            var after = await KeyIdentifiers(keyPath);

            Assert.NotEmpty(before);
            Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(keyPath))
            {
                Directory.Delete(keyPath, recursive: true);
            }
        }
    }

    private static async Task<List<string>> KeyIdentifiers(string keyPath)
    {
        // A fresh factory is a fresh process as far as the keys are concerned.
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("StubId:KeyPath", keyPath));

        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/op/.well-known/openid-configuration/jwks", Ct));

        return document.RootElement.GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()!)
            .ToList();
    }
}
