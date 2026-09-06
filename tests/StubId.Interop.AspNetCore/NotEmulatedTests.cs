using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// What an endpoint answers when the discovery document advertises it and this build does not
/// reproduce it.
/// </summary>
/// <remarks>
/// The README has promised for some time that these answer "501 with a link to the reason rather
/// than a misleading 404". The mechanism existed, the provenance existed, and no route had ever
/// carried it - so the one endpoint this applies to answered 404 and the divergences said so, in
/// the same repository as the README. These tests are what makes the sentence true rather than
/// aspirational, which is why they assert on the link and not only on the status.
/// </remarks>
public class NotEmulatedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NotEmulatedTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The backchannel authentication endpoint says what it is, on either method.
    /// </summary>
    /// <remarks>
    /// POST is what CIBA uses and GET is what somebody exploring will try. Both answer the same
    /// thing, because a 405 would say the method was wrong when the truth is that the endpoint is
    /// not reproduced at all.
    /// </remarks>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task The_endpoint_discovery_advertises_and_this_build_skips_answers_501(string method)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/op/connect/ciba");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal("not_implemented", body.RootElement.GetProperty("error").GetString());
        Assert.Contains(
            "/op/connect/ciba",
            body.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason is a link, and it goes to the section that explains the decision.
    /// </summary>
    /// <remarks>
    /// A path from the root of the repository is what the ledger carries and what the build can
    /// check, but it is not something a caller reading a log can open. The answer resolves it, and
    /// this checks both halves: that what comes back is absolute, and that the tail of it is still
    /// a real section in a real document.
    /// </remarks>
    [Fact]
    public async Task The_reason_it_sends_back_is_a_link_to_a_section_that_exists()
    {
        using var client = _factory.CreateClient();

        // Not GetStringAsync: it throws on anything but a success status, and 501 is the point.
        using var response = await client.GetAsync("/op/connect/ciba", Ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        var reason = body.RootElement.GetProperty("reason").GetString()!;

        Assert.StartsWith("https://github.com/benne/stubid/blob/master/", reason, StringComparison.Ordinal);

        var (path, anchor) = Split(reason);

        Assert.True(File.Exists(Path.Combine(Root(), path)), $"{path} is not in the tree.");
        Assert.Contains(
            $"<a id=\"{anchor}\"",
            File.ReadAllText(Path.Combine(Root(), path)),
            StringComparison.Ordinal);
    }

    /// <summary>A running instance reports it, so the ledger and the route agree.</summary>
    [Fact]
    public async Task The_ledger_a_running_instance_serves_carries_it()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/_stubid/v1/fidelity", Ct));

        var unemulated = document.RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("provenance").GetString() == "NotEmulated")
            .ToList();

        Assert.NotEmpty(unemulated);
        Assert.All(unemulated, e => Assert.False(
            string.IsNullOrEmpty(e.GetProperty("reason").GetString()),
            "Nothing unemulated may be in the ledger without saying where the reason is."));
    }

    /// <summary>
    /// The route table says which routes are answered and which are only advertised.
    /// </summary>
    /// <remarks>
    /// So a suite can find out without reading the ledger or knowing the path by heart. Every
    /// other route is emulated, and asserting that too is the half that would catch an annotation
    /// landing on the wrong handler.
    /// </remarks>
    [Fact]
    public async Task The_route_table_marks_it_as_one_this_build_does_not_answer()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/_stubid/v1/routes", Ct));

        var routes = document.RootElement.GetProperty("routes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("pattern").GetString()!,
                          r => r.GetProperty("emulated").GetBoolean());

        Assert.False(routes["/op/connect/ciba"]);
        Assert.All(routes.Where(r => r.Key != "/op/connect/ciba"), r => Assert.True(
            r.Value, $"{r.Key} is marked as not emulated, and it should be."));
    }

    /// <summary>
    /// A route from an instance older than this field is read as emulated, not as unemulated.
    /// </summary>
    /// <remarks>
    /// The package and the image version independently, and the guides say plainly that a client
    /// may be pointed at an instance somebody else is running. So a new client will meet a server
    /// that sends no <c>emulated</c> key, and what it reads then has to be the safe answer.
    /// <para>
    /// It is not enough that the property is declared with a default. This client serializes
    /// through a source-generated context, for trim and AOT safety, and that generator writes an
    /// absent member as an unconditional object-initializer slot rather than leaving the field
    /// initializer alone. A missing key therefore lands as <c>default(bool)</c>. The reflection
    /// path gets it right and the shipped path does not, which is exactly the sort of difference
    /// no amount of reading the record declaration reveals.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_route_from_an_instance_older_than_this_field_reads_as_emulated()
    {
        const string Old = """
            {"routes":[{"pattern":"/op/connect/token","methods":["POST"],"role":"token"}]}
            """;

        using var http = new HttpClient(new Canned(Old)) { BaseAddress = new Uri("http://stubid.invalid") };
        using var client = new StubId.Client.StubIdClient(http);

        var routes = await client.RoutesAsync(Ct);

        Assert.True(routes[0].Emulated,
            "An instance that predates the field says nothing about it, and silence has to mean "
            + "the route is answered. Reading it as unemulated inverts the truth for every route "
            + "at once.");
    }

    /// <summary>One canned response, so a payload shape can be tested without a server that sends it.</summary>
    private sealed class Canned(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static (string Path, string Anchor) Split(string reason)
    {
        var tail = reason["https://github.com/benne/stubid/blob/master/".Length..].Split('#');

        Assert.True(tail.Length == 2, $"{reason} names no section.");

        return (tail[0], tail[1]);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StubID.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
