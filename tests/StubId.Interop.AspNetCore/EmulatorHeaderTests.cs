using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Every response says that it came from an emulator.
/// </summary>
/// <remarks>
/// <para>
/// TRADEMARKS.md states this as an undertaking, not a feature: "Every response carries an
/// <c>X-StubID-Emulator</c> header so an instance cannot be mistaken for a production system."
/// It sat in that file from the repository's first commit, written before there was a server to
/// put it in, and no code emitted it until this test existed.
/// </para>
/// <para>
/// The header name is written out here rather than read from a constant. A test that asks the
/// server what it calls its own header agrees with the server by construction; what has to hold
/// is that the bytes match the two documents that promise them.
/// </para>
/// </remarks>
public class EmulatorHeaderTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory
        .WithWebHostBuilder(b => b.UseSetting("StubId:PublicBaseUrl", "http://localhost"))
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// One path per way this server can answer, because they do not share a code path.
    /// </summary>
    public static TheoryData<string, string> EveryKindOfAnswer() => new()
    {
        { "/op/.well-known/openid-configuration", "a document served from a recording" },
        { "/nope", "a path the broker does not have" },
        { "/_stubid/v1/fidelity", "the control API, which is not the broker's surface" },
        { "/op/connect/userinfo", "a challenge, answered without a route being run" },
        { "/op/Login?session=nothing-parked-here", "a page rendered for a human" },
        { "/op/Error?errorId=nonsense", "the error page" },
        { "/_stubid/admin", "StubID's own page, which is not the broker's either" },
    };

    [Theory]
    [MemberData(nameof(EveryKindOfAnswer))]
    public async Task Every_kind_of_answer_carries_the_header(string path, string what)
    {
        // The path gate refuses by setting a status and returning, so it never reaches the
        // routes and never reaches anything registered after it. A header added anywhere but
        // the front of the pipeline is on the responses that were easy and missing from the
        // ones that were not.
        using var response = await _client.GetAsync(path, Ct);

        Assert.True(response.Headers.TryGetValues("X-StubID-Emulator", out var values),
            $"{what} ({path}, {(int)response.StatusCode}) carried no X-StubID-Emulator header.");

        Assert.Equal(["1"], values!);
    }

    [Fact]
    public async Task The_header_is_not_the_brokers_and_is_the_only_one_StubID_adds()
    {
        // The recordings are what everything else here is measured against, and none of them
        // carries this header - which is the point of it. Adding a second one later is how an
        // emulator starts to be told apart from the real thing by accident rather than on
        // purpose, so the count is asserted and not just the presence.
        using var response = await _client.GetAsync(
            "/op/.well-known/openid-configuration", Ct);

        var announced = response.Headers
            .Where(h => h.Key.StartsWith("X-StubID", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(announced);
        Assert.Equal("X-StubID-Emulator", announced[0].Key);
    }
}
