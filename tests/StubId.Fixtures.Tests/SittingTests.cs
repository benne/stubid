using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What a sitting sends, checked without anybody sitting down.
/// </summary>
/// <remarks>
/// <para>
/// A step that the broker refuses is found by <c>rehearse</c> at best, which needs the network and
/// the account and cannot say why; at worst it is found in the chair. These hold the catalog to the
/// rules a refusal would otherwise be the first sign of.
/// </para>
/// <para>
/// The key is generated per run and the settings are made up, so nothing here reads the machine's
/// configuration. In the collection because building a step reads settings, which these set.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class SittingTests : IDisposable
{
    private const string Domain = "harbourline";
    private const string KeyId = "key-for-tests";

    private readonly string directory = Directory.CreateTempSubdirectory("stubid-sitting-").FullName;

    private readonly RSA key = RSA.Create(2048);

    public void Dispose()
    {
        key.Dispose();
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (string Name, string Value)[] SignicatSettings()
    {
        var path = Path.Combine(directory, "signing.pem");
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());

        return
        [
            ("STUBID_SIGNICAT_DOMAIN", Domain),
            ("STUBID_SIGNICAT_PRIVATE_KEY_PATH", path),
            ("STUBID_SIGNICAT_KEY_ID", KeyId),
            .. BrokerClient.Signicat.All.SelectMany(client => new[]
            {
                (client.IdSetting!, $"{client.Name}-client-for-tests"),
                (client.SecretSetting, "not-a-real-secret"),
            }),
        ];
    }

    private string Built(ManualCase step) =>
        ProcessEnvironment.With(() => Session.BuildAuthorize(BrokerTarget.Signicat, step).Url, SignicatSettings());

    private static Dictionary<string, string> Query(string url) => new Uri(url).Query
        .TrimStart('?')
        .Split('&')
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(
            pair => Uri.UnescapeDataString(pair[0]),
            pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "",
            StringComparer.Ordinal);

    private static JsonElement Segment(string compact, int index) => JsonDocument
        .Parse(Base64Url.DecodeFromChars(compact.Split('.')[index]))
        .RootElement.Clone();

    /// <summary>What the broker reads: the query, or the object where the step signs one.</summary>
    private static Dictionary<string, string> Sent(string url)
    {
        var query = Query(url);
        return query.TryGetValue("request", out var request)
            ? Segment(request, 1).EnumerateObject()
                .Where(member => member.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(member => member.Name, member => member.Value.GetString()!, StringComparer.Ordinal)
            : query;
    }

    private static ManualCase Step(string id) => ManualCatalog.For(Broker.Signicat).Single(c => c.Id == id);

    public static TheoryData<string> SignicatSteps()
    {
        var data = new TheoryData<string>();
        foreach (var step in ManualCatalog.For(Broker.Signicat))
        {
            data.Add(step.Id);
        }

        return data;
    }

    [Fact]
    public void Every_step_on_a_client_that_requires_a_request_object_signs_one()
    {
        var unsigned = Enum.GetValues<Broker>()
            .SelectMany(ManualCatalog.For)
            .Where(step => step.Client.RequiresRequestObject && !step.SignRequest)
            .Select(step => $"{step.Id} on {step.Client.Broker}'s {step.Client.Name} client")
            .ToList();

        Assert.True(unsigned.Count == 0, "These would be refused before the login page: " + string.Join(", ", unsigned));
    }

    [Fact]
    public void No_step_asks_for_a_follow_up_its_broker_has_no_endpoint_for()
    {
        var unanswerable = BrokerTarget.All
            .Where(target => target.CprMatchPath is null)
            .SelectMany(target => ManualCatalog.For(target.Broker))
            .Where(step => step.FollowUps.Contains(FollowUp.CprMatch))
            .Select(step => step.Id)
            .ToList();

        Assert.Empty(unanswerable);
    }

    /// <remarks>
    /// <c>--only</c> filters both catalogs by id, so an id in both would start a sitting step and an
    /// unattended case from one command.
    /// </remarks>
    [Fact]
    public void No_capture_id_is_both_a_step_and_an_unattended_case()
    {
        foreach (var target in BrokerTarget.All)
        {
            Assert.Empty(ManualCatalog.For(target.Broker).Select(step => step.Id)
                .Intersect(CaptureCatalog.For(target.Broker).Select(@case => @case.Id)));
        }
    }

    /// <summary>
    /// Every step on the second broker sends its login to MitID, in the parameter that broker reads.
    /// </summary>
    /// <remarks>
    /// A step's own acr_values replaces the target's rather than adding to it, which is how the
    /// consent step could drop <c>idp:mitid</c> without anything saying so. The first broker's
    /// <c>idp_values</c> is ignored here and must not be sent either.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SignicatSteps))]
    public void Every_second_broker_step_selects_MitID_in_acr_values(string id)
    {
        var url = Built(Step(id));
        var sent = Sent(url);

        Assert.DoesNotContain("idp_values", Query(url).Keys);
        Assert.DoesNotContain("idp_values", sent.Keys);
        Assert.Contains("idp:mitid", sent["acr_values"].Split(' '));
    }

    /// <summary>
    /// The first broker's steps send what its sitting recorded, parameter for parameter.
    /// </summary>
    /// <remarks>
    /// Against the committed recording rather than a list written here: moving the MitID selection
    /// onto the target is exactly the change that could reorder or rename a parameter the first
    /// broker's recordings were made with.
    /// </remarks>
    [Fact]
    public void The_first_broker_still_sends_what_its_sitting_recorded()
    {
        var step = ManualCatalog.For(Broker.NetsEidBroker).Single(c => c.Id == "CAP-020");
        var url = ProcessEnvironment.With(
            () => Session.BuildAuthorize(BrokerTarget.NetsEidBroker, step).Url,
            ("STUBID_NEB_PP_CLIENT_ID", "00000000-0000-0000-0000-000000000000"),
            ("STUBID_NEB_PP_CLIENT_SECRET", "not-a-real-secret"));

        using var recorded = JsonDocument.Parse(File.ReadAllText(Repository.SessionFixture("CAP-020", "callback", "meta.json")));
        var recordedUrl = recorded.RootElement.GetProperty("request").GetProperty("url").GetString()!;

        Assert.Equal(Query(recordedUrl).Keys, Query(url).Keys);
        Assert.Equal("mitid", Query(url)["idp_values"]);
    }

    /// <remarks>
    /// A form_post step is answered with a page, not a redirect, and the rehearsal reads where that
    /// page posts to. Written both ways round because the order of an input's attributes is the
    /// server's choice.
    /// </remarks>
    [Theory]
    [InlineData("<form method='post' action='http://localhost:5099/callback'><input type='hidden' name='state' value='CAP-029' /></form>")]
    [InlineData("<FORM action=\"http://localhost:5099/callback\" method=\"post\"><input value=\"CAP-029\" type=\"hidden\" name=\"state\"></FORM>")]
    public void A_form_post_page_says_where_it_posts_and_with_which_state(string html)
    {
        Assert.Equal(("http://localhost:5099/callback", "CAP-029"), Rehearsal.FormPostTarget(html));
    }

    [Fact]
    public void A_page_with_no_form_names_no_target()
    {
        Assert.Equal((null, null), Rehearsal.FormPostTarget("<html><body>An error has occured</body></html>"));
    }

    private static ManualCase Waiting(string id, bool expectCode) => new()
    {
        Id = id,
        Step = "Step 0",
        Title = "A step outstanding",
        Settles = "Nothing; this one is a test.",
        Operator = "Nothing.",
        Client = BrokerClient.Signicat.Partner,
        Scope = "openid",
        ExpectCode = expectCode,
    };

    [Fact]
    public void A_callback_belongs_to_the_step_its_state_names()
    {
        Assert.Equal("CAP-023", Session.CallbackStep("CAP-023", [Waiting("CAP-022", false), Waiting("CAP-023", true)]));
    }

    /// <remarks>
    /// The timeout step waits underneath most of a sitting. A reloaded callback from a step already
    /// recorded was filed under it, and the timeout's own answer then matched nothing.
    /// </remarks>
    [Fact]
    public void A_callback_for_a_step_already_consumed_is_not_handed_to_the_step_still_waiting()
    {
        Assert.Null(Session.CallbackStep("CAP-023", [Waiting("CAP-022", false)]));
        Assert.Null(Session.CallbackStep("CAP-023", [Waiting("CAP-024", true)]));
    }

    [Fact]
    public void Without_state_only_the_one_step_expecting_a_code_takes_the_callback()
    {
        Assert.Equal("CAP-023", Session.CallbackStep(null, [Waiting("CAP-022", false), Waiting("CAP-023", true)]));
        Assert.Null(Session.CallbackStep(null, [Waiting("CAP-022", false)]));
        Assert.Null(Session.CallbackStep(null, [Waiting("CAP-023", true), Waiting("CAP-024", true)]));
    }

    /// <remarks>
    /// Both brokers' answers to a code that is not real: the second describes its errors and the
    /// first does not, and neither difference may change the verdict.
    /// </remarks>
    [Theory]
    [InlineData(400, """{"error":"invalid_grant","error_uri":"https://example.invalid/e","error_description":"The provided authorization grant is invalid."}""", "ready")]
    [InlineData(400, """{"error":"invalid_grant"}""", "ready")]
    [InlineData(400, """{"error":"invalid_client","error_description":"Client authentication failed."}""", "PROBLEM")]
    [InlineData(400, """{"error":"unauthorized_client"}""", "PROBLEM")]
    [InlineData(502, "<html>Bad gateway</html>", "PROBLEM")]
    public void A_secret_is_good_when_only_the_code_is_refused(int status, string body, string verdict)
    {
        Assert.Equal(verdict, Rehearsal.SecretVerdict(status, body).Verdict);
    }

    [Fact]
    public void A_signed_step_is_signed_with_the_registered_key_for_the_tenant()
    {
        var url = Built(Step("CAP-023"));
        var query = Query(url);

        Assert.Equal(["client_id", "request", "response_type"], query.Keys.Order(StringComparer.Ordinal));

        var compact = query["request"];
        var header = Segment(compact, 0);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal(KeyId, header.GetProperty("kid").GetString());

        var parts = compact.Split('.');
        Assert.True(key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        var payload = Segment(compact, 1);
        Assert.Equal($"https://{Domain}.sandbox.signicat.com/auth/open", payload.GetProperty("aud").GetString());
        Assert.Equal("primary-client-for-tests", payload.GetProperty("iss").GetString());
        Assert.Equal("openid profile", payload.GetProperty("scope").GetString());
        Assert.Equal("CAP-023", payload.GetProperty("state").GetString());
        Assert.True(payload.TryGetProperty("code_challenge", out _));
    }
}
