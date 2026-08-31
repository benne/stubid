using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Replays every recorded request that needs no login, and checks StubID answers it the way
/// the broker did.
/// </summary>
/// <remarks>
/// <para>
/// The disposition is the thing worth pinning, not the status code. A refused request and an
/// accepted one are both a 302, and telling them apart is the whole difference between a
/// client that sees an error and a client that sees nothing at all. Getting that backwards
/// produces an emulator that passes every test a client library can express while being
/// unfaithful in the one way that matters to whoever is debugging at two in the morning.
/// </para>
/// <para>
/// Driven from the fixtures rather than from a hand-written table: a table is prose about the
/// recordings, and prose about the recordings has been wrong here before.
/// </para>
/// </remarks>
public class RecordedDispositionTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RedirectUri = "http://localhost:5099/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("StubId:PublicBaseUrl", "http://localhost");
            b.UseSetting("StubId:ApproveAutomatically", "false");
        })
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Every unattended recording, replayed. Cases whose answer depends on credentials StubID
    /// does not share with the broker are excluded by name below, with the reason.
    /// </summary>
    public static TheoryData<string> Recorded()
    {
        var data = new TheoryData<string>();

        foreach (var directory in Directory
            .EnumerateDirectories(Path.Combine(Root(), "fixtures", "neb", "pp"))
            .OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);

            // Discovery, JWKS and the error-code catalogue are documents, checked elsewhere by
            // their bytes. CAP-006 is production rather than pre-production.
            if (id is "CAP-001" or "CAP-002" or "CAP-006" or "CAP-007")
            {
                continue;
            }

            // The alternate discovery layouts are 404s, checked by PathRules.
            if (id is "CAP-003" or "CAP-004" or "CAP-005")
            {
                continue;
            }

            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Recorded))]
    public async Task StubId_answers_the_way_the_broker_did(string id)
    {
        var directory = Path.Combine(Root(), "fixtures", "neb", "pp", id);
        using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "request.json"), Ct));

        var root = recorded.RootElement;
        var url = new Uri(root.GetProperty("url").GetString()!);
        var method = new HttpMethod(root.GetProperty("method").GetString()!);

        using var request = new HttpRequestMessage(method, url.PathAndQuery);

        Attach(request, root);

        using var response = await _client.SendAsync(request, Ct);

        var expected = Disposition(
            (int)ReadStatus(directory),
            Header(await File.ReadAllLinesAsync(Path.Combine(directory, "response.head"), Ct), "Location"),
            await File.ReadAllTextAsync(Path.Combine(directory, "response.raw"), Ct));

        var actual = Disposition(
            (int)response.StatusCode,
            response.Headers.Location?.ToString(),
            await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A bare OAuth error is the one case where the bytes themselves are the contract, so the
    /// body is compared rather than classified.
    /// </summary>
    [Theory]
    [InlineData("CAP-015")]
    [InlineData("CAP-016")]
    [InlineData("CAP-019")]
    [InlineData("CAP-042")]
    public async Task A_refused_request_carries_the_recorded_error_and_nothing_else(string id)
    {
        var directory = Path.Combine(Root(), "fixtures", "neb", "pp", id);
        using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "request.json"), Ct));

        var root = recorded.RootElement;
        using var request = new HttpRequestMessage(
            new HttpMethod(root.GetProperty("method").GetString()!),
            new Uri(root.GetProperty("url").GetString()!).PathAndQuery);

        Attach(request, root);

        using var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(
            (await File.ReadAllTextAsync(Path.Combine(directory, "response.raw"), Ct)).Trim(),
            body.Trim());

        // No description, no error_uri. The broker sends neither, and a client that logs the
        // whole body would show a StubID-authored sentence the broker never sends.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("error_description", body, StringComparison.Ordinal);

        var headers = await File.ReadAllLinesAsync(Path.Combine(directory, "response.head"), Ct);
        Assert.Equal(
            Header(headers, "Cache-Control"),
            string.Join(", ", response.Headers.CacheControl?.ToString() ?? ""));
    }

    /// <summary>
    /// CAP-014 is the one recording StubID deliberately does not match, so it is asserted
    /// rather than skipped.
    /// </summary>
    /// <remarks>
    /// The broker refuses a wrong secret with invalid_client. StubID accepts any non-empty
    /// secret, because a stub cannot know the secret an existing configuration already
    /// carries, and demanding a particular one defeats the point of changing only the
    /// authority. Written down in docs/brokers/neb/divergences.md, and here, so that closing
    /// the gap later means deleting a test that says what it was for rather than discovering
    /// an assertion nobody can explain.
    /// </remarks>
    [Fact]
    public async Task A_wrong_secret_is_accepted_which_is_the_one_divergence_here()
    {
        var directory = Path.Combine(Root(), "fixtures", "neb", "pp", "CAP-014");
        using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "request.json"), Ct));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/op/connect/token");
        Attach(request, recorded.RootElement);

        using var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(
            """{"error":"invalid_client"}""",
            (await File.ReadAllTextAsync(Path.Combine(directory, "response.raw"), Ct)).Trim());

        // The secret got through; what failed was the code, which is the next check along.
        Assert.Equal("""{"error":"invalid_grant"}""", body.Trim());

        // A missing secret is still refused. Telling "authenticated badly" from "did not
        // authenticate at all" is the part of the behaviour worth keeping.
        using var anonymous = await _client.PostAsync("/op/connect/token", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("grant_type", "authorization_code"),
             new KeyValuePair<string, string>("client_id", "0a775a87-878c-4b83-abe3-ee29c720c3e7")]), Ct);

        Assert.Equal(
            """{"error":"invalid_client"}""",
            (await anonymous.Content.ReadAsStringAsync(Ct)).Trim());
    }

    /// <summary>The two challenges differ from each other, byte for byte, and both are real.</summary>
    [Theory]
    [InlineData("CAP-017", "/op/connect/userinfo")]
    [InlineData("CAP-018", "/op/api/v1/mitid/matchCpr")]
    public async Task An_unauthenticated_call_is_challenged_the_way_it_was_recorded(string id, string path)
    {
        var headers = await File.ReadAllLinesAsync(
            Path.Combine(Root(), "fixtures", "neb", "pp", id, "response.head"), Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            Header(headers, "WWW-Authenticate"),
            response.Headers.WwwAuthenticate.ToString());

        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// Replays the recorded body as it was sent. A recording carries either the raw body or
    /// the fields it was built from, and an empty body is a case in its own right - the token
    /// endpoint answers one way to no parameters and another way to wrong ones.
    /// </summary>
    private static void Attach(HttpRequestMessage request, JsonElement recorded)
    {
        if (recorded.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
        {
            request.Content = new StringContent(
                body.GetString() ?? "", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            return;
        }

        if (recorded.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object)
        {
            request.Content = new FormUrlEncodedContent(form.EnumerateObject()
                .Select(f => new KeyValuePair<string, string>(f.Name, f.Value.GetString() ?? "")));
        }
    }

    /// <summary>
    /// Classifies an answer the way the capture harness does: what the broker did with the
    /// request, rather than which number it returned.
    /// </summary>
    private static string Disposition(int status, string? location, string body)
    {
        if (location?.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase) == true)
        {
            return "back to the client";
        }

        return status switch
        {
            404 => "not served",
            401 => "challenged",
            >= 300 and < 400 when location?.Contains("/Error?errorId=", StringComparison.Ordinal) == true
                => "the broker's error page",

            // StubID's own page stands where the broker's authenticator does. Both mean the
            // request was accepted and the person is being asked something.
            >= 300 and < 400 when location?.Contains("/Account/Login", StringComparison.Ordinal) == true
                || location?.Contains("/op/Login", StringComparison.Ordinal) == true
                => "on to the authenticator",
            >= 300 and < 400 when location?.Contains("/Logout", StringComparison.Ordinal) == true
                => "on to the logout page",
            >= 400 when body.TrimStart().StartsWith("{\"error\":", StringComparison.Ordinal)
                => "refused with a bare error",
            >= 200 and < 300 => "answered",
            _ => $"unclassified {status}",
        };
    }

    private static HttpStatusCode ReadStatus(string directory) =>
        (HttpStatusCode)int.Parse(File.ReadAllLines(Path.Combine(directory, "response.head"))[0]
            .Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

    private static string? Header(string[] headers, string name) => headers
        .FirstOrDefault(h => h.StartsWith($"{name}: ", StringComparison.OrdinalIgnoreCase))
        ?[(name.Length + 2)..];

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
