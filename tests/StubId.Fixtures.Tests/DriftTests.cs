using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What <c>verify</c> calls drift, on bodies that name a configured value and on a key set that
/// changes from one request to the next.
/// </summary>
/// <remarks>
/// Both showed up the first time the second broker's pack was verified, a minute after it was
/// recorded: the discovery document and the key set drifted and nothing else did. In the collection
/// because comparing scrubs, and this sets a setting the scrub reads.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class DriftTests
{
    private const string Domain = "harbourline";

    private static CaptureCase Case(BrokerTarget target, string id) =>
        CaptureCatalog.For(target.Broker).Single(c => c.Id == id);

    private static CaptureCase KeySet(BrokerTarget target) => Case(target, target.KeySetCaptureId);

    private static RecordedExchange Served(byte[] body) =>
        new("GET", "https://example.invalid/", [], null, 200, "OK", [], body);

    private static RecordedExchange Served(string body) => Served(Encoding.UTF8.GetBytes(body));

    private static string Issuer(string domain) =>
        $"{{\"issuer\":\"https://{domain}.sandbox.signicat.com/auth/open\"}}";

    private static string Key(string kid, string extra = "") =>
        "{\"kty\":\"RSA\",\"use\":\"sig\",\"kid\":\"" + kid
        + "\",\"e\":\"AQAB\",\"n\":\"bW9kdWx1cw\",\"alg\":\"RS256\"" + extra + "}";

    private static string Keys(params string[] keys) => $"{{\"keys\":[{string.Join(',', keys)}]}}";

    [Fact]
    public void A_body_naming_a_configured_value_matches_its_own_recording() => ProcessEnvironment.With(() =>
    {
        var discovery = Case(BrokerTarget.Signicat, "CAP-001");
        var committed = FixtureStore.ScrubBody(Encoding.UTF8.GetBytes(Issuer(Domain)));

        Assert.Contains("{{SIGNICAT_DOMAIN}}", Encoding.UTF8.GetString(committed), StringComparison.Ordinal);
        Assert.True(Normalizer.BodyMatches(committed, Served(Issuer(Domain)), discovery));
        Assert.False(Normalizer.BodyMatches(committed, Served(Issuer("elsewhere")), discovery));
    }, ("STUBID_SIGNICAT_DOMAIN", Domain));

    /// <remarks>
    /// Against the committed file, so a recording whose keys have a shape the patterns do not know
    /// fails here rather than as drift on somebody's next run.
    /// </remarks>
    [Fact]
    public void Every_key_the_pack_recorded_is_the_shape_verify_promises()
    {
        var keySet = KeySet(BrokerTarget.Signicat);
        var recorded = File.ReadAllBytes(Repository.Fixture(BrokerTarget.Signicat, keySet.Id, "response.raw"));

        Assert.Equal("""{"keys":[<volatile>]}""", Normalizer.NormalizeBody(Served(recorded), keySet));
    }

    [Fact]
    public void Other_keys_in_another_order_match_and_a_key_of_another_shape_does_not() => ProcessEnvironment.With(() =>
    {
        var keySet = KeySet(BrokerTarget.Signicat);
        var committed = Encoding.UTF8.GetBytes(Keys(Key("a"), Key("b")));

        Assert.True(Normalizer.BodyMatches(committed, Served(Keys(Key("c"), Key("b"), Key("d"))), keySet));
        Assert.False(Normalizer.BodyMatches(
            committed, Served(Keys(Key("a"), Key("b", ",\"x5c\":[\"Y2VydA\"]"))), keySet));
    });

    [Fact]
    public void A_list_compared_as_a_set_matches_in_another_order_and_not_with_another_entry() => ProcessEnvironment.With(() =>
    {
        var discovery = Case(BrokerTarget.Signicat, "CAP-001");
        var committed = Encoding.UTF8.GetBytes("""{"claims_supported":["a","b","c"],"issuer":"x"}""");

        Assert.True(Normalizer.BodyMatches(committed, Served("""{"claims_supported":["c","a","b"],"issuer":"x"}"""), discovery));
        Assert.False(Normalizer.BodyMatches(committed, Served("""{"claims_supported":["a","b","d"],"issuer":"x"}"""), discovery));

        // Only where a case says so. The first broker's discovery document names no such list.
        Assert.False(Normalizer.BodyMatches(
            committed, Served("""{"claims_supported":["c","a","b"],"issuer":"x"}"""), Case(BrokerTarget.NetsEidBroker, "CAP-001")));
    });

    public static TheoryData<string, string> CasesWithUnorderedArrays()
    {
        var data = new TheoryData<string, string>();
        foreach (var target in BrokerTarget.All)
        {
            foreach (var @case in CaptureCatalog.For(target.Broker).Where(c => c.UnorderedArrays.Count > 0))
            {
                data.Add(target.Key, @case.Id);
            }
        }

        return data;
    }

    /// <remarks>
    /// A misspelled member name sorts nothing and fails nothing, and the case goes back to
    /// comparing the list in order - which reads as drift the day the broker reorders it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(CasesWithUnorderedArrays))]
    public void Every_list_a_case_compares_as_a_set_is_in_its_recording(string broker, string id)
    {
        var target = BrokerTarget.Select(broker);
        using var recorded = JsonDocument.Parse(File.ReadAllBytes(Repository.Fixture(target, id, "response.raw")));

        // Whether the list is there, not whether it is out of order: the broker may one day serve it
        // sorted, and that would not make the name wrong.
        foreach (var member in Case(target, id).UnorderedArrays)
        {
            Assert.True(
                recorded.RootElement.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array,
                $"{id} compares {member} as a set, and its recording holds no such array.");
        }
    }

    public static TheoryData<string> Brokers()
    {
        var data = new TheoryData<string>();
        foreach (var target in BrokerTarget.All)
        {
            data.Add(target.Key);
        }

        return data;
    }

    /// <summary>
    /// The target's claim about its key set and the catalog's masking of it are one fact, declared
    /// twice.
    /// </summary>
    /// <remarks>
    /// A broker declared to vary with a key-set case that masks nothing drifts on most runs; one
    /// declared stable with a case that masks its keys has stopped checking which keys it serves.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Brokers))]
    public void A_key_set_case_masks_its_keys_exactly_where_the_broker_varies_them(string broker)
    {
        var target = BrokerTarget.Select(broker);

        Assert.Equal(target.KeySetVariesPerRequest, KeySet(target).VolatileBodyPatterns.Count > 0);
    }
}
