using System.Net;

namespace StubId.InProcess.Tests;

/// <summary>
/// The admin pages are part of the one composition, so they are here as well as in the container.
/// </summary>
/// <remarks>
/// This is a small test guarding a large claim. An in-process host runs on a test server: there is
/// no socket, no port and no address for the process to dial itself on. So a page that reached its
/// own control API over HTTP - which is the obvious way to write one, and the way that keeps the
/// UI honest about using the same surface a test uses - would work in the container and be
/// impossible here.
/// <para>
/// The pages read the services directly for that reason, and share
/// <c>Approvals</c> with the control API rather than a transport. If anybody ever reaches for an
/// HttpClient inside a handler, this is what fails.
/// </para>
/// <para>
/// Nothing here can open the page in a browser. That is the documented limit of this module, not
/// a gap in the test: <c>docs/guides/in-process.md</c> says anything that has to dial StubID needs
/// the container.
/// </para>
/// </remarks>
public class AdminPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_admin_page_is_served_in_process_too()
    {
        await using var stub = new StubIdHostBuilder().WithAutomaticApproval(false).Build();
        await stub.StartAsync(Ct);

        using var client = stub.CreateClient();
        using var page = await client.GetAsync("/_stubid/admin", Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType?.ToString());

        Assert.Contains(
            "StubID is an emulator",
            await page.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }
}
