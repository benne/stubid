using System.Text.Json;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the recorded discovery document obliges StubID to emit.
/// </summary>
/// <remarks>
/// The absences matter as much as the values. A client library keys off what metadata is
/// there, so a document that helpfully fills in what the broker omits is less faithful than
/// one that reproduces the gaps.
/// </remarks>
public class DiscoveryContractTests
{
    private static JsonElement Discovery(string captureId)
    {
        var json = File.ReadAllText(Repository.Fixture(captureId, "response.raw"));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Theory]
    [InlineData("scopes_supported")]
    [InlineData("claims_supported")]
    [InlineData("acr_values_supported")]
    public void The_broker_omits_these_and_so_must_we(string member)
    {
        Assert.False(Discovery("CAP-001").TryGetProperty(member, out _));
    }

    [Theory]
    [InlineData("revocation_endpoint_auth_methods_supported")]
    [InlineData("revocation_endpoint_auth_signing_alg_values_supported")]
    [InlineData("introspection_endpoint_auth_methods_supported")]
    [InlineData("introspection_endpoint_auth_signing_alg_values_supported")]
    public void Authentication_is_advertised_for_endpoints_that_are_never_published(string member)
    {
        Assert.True(Discovery("CAP-001").TryGetProperty(member, out _));
    }

    [Theory]
    [InlineData("revocation_endpoint")]
    [InlineData("introspection_endpoint")]
    public void The_endpoints_themselves_are_absent(string member)
    {
        Assert.False(Discovery("CAP-001").TryGetProperty(member, out _));
    }

    [Fact]
    public void The_issuer_carries_a_path_segment()
    {
        // Load-bearing: openid-client and Spring both compare the discovered issuer against
        // the configured authority, so the /op suffix has to survive into every token.
        Assert.Equal(
            "https://pp.netseidbroker.dk/op",
            Discovery("CAP-001").GetProperty("issuer").GetString());
    }

    [Fact]
    public void Only_RS256_signs_an_id_token()
    {
        var algorithms = Discovery("CAP-001")
            .GetProperty("id_token_signing_alg_values_supported")
            .EnumerateArray().Select(a => a.GetString() ?? "").ToArray();

        Assert.Equal(new[] { "RS256" }, algorithms);
    }

    [Fact]
    public void Pushed_authorisation_is_advertised_so_dotnet_clients_will_use_it()
    {
        // ASP.NET Core defaults to UseIfAvailable, which makes PAR the first protocol
        // request a stock client sends. It cannot be deferred.
        Assert.True(Discovery("CAP-001").TryGetProperty("pushed_authorization_request_endpoint", out _));
        Assert.False(Discovery("CAP-001").GetProperty("require_pushed_authorization_requests").GetBoolean());
    }

    [Fact]
    public void Every_authorization_response_must_carry_iss()
    {
        // Advertising this obliges us: oauth4webapi rejects a response without iss once the
        // metadata says the parameter is supported.
        Assert.True(Discovery("CAP-001")
            .GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
    }

    [Fact]
    public void Production_differs_from_pre_production_only_by_host()
    {
        // This is what lets one profile serve both environments.
        var preProduction = File.ReadAllText(Repository.Fixture("CAP-001", "response.raw"));
        var production = File.ReadAllText(Repository.Fixture("CAP-006", "response.raw"));

        Assert.Equal(
            preProduction,
            production.Replace("https://netseidbroker.dk", "https://pp.netseidbroker.dk", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CAP-003")]
    [InlineData("CAP-004")]
    [InlineData("CAP-005")]
    public void Alternate_metadata_layouts_are_not_served(string captureId)
    {
        // Serving these would let a misconfigured client pass against StubID and fail
        // against the broker, which is the failure this project exists to prevent.
        var head = File.ReadAllText(Repository.Fixture(captureId, "response.head"));
        Assert.StartsWith("HTTP 404", head, StringComparison.Ordinal);
    }
}
