using System.Text;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What a broker's answer is called, and what happens where nobody has seen one yet.
/// </summary>
/// <remarks>
/// Two literals used to decide this — the error page's path and the login page's — and they were
/// spelled out in the classifier and in the rehearsal both. They belong to the broker, and one of
/// them has never been observed on the second.
/// </remarks>
public class ClassificationTests
{
    private static RecordedExchange Redirect(int status, string location) => new(
        "GET",
        "https://example.invalid/connect/authorize",
        [],
        null,
        status,
        "Found",
        [new KeyValuePair<string, string>("Location", location)],
        []);

    private static RecordedExchange Body(int status, string json) => new(
        "POST", "https://example.invalid/connect/token", [], null, status, "Bad Request",
        [new KeyValuePair<string, string>("Content-Type", "application/json")],
        Encoding.UTF8.GetBytes(json));

    [Fact]
    public void An_accepted_authorize_is_a_login_redirect_where_the_broker_declares_one()
    {
        Assert.Equal(
            Disposition.LoginRedirect,
            DispositionClassifier.Classify(
                Redirect(302, "https://pp.netseidbroker.dk/op/Account/Login?ReturnUrl=x"),
                BrokerTarget.NetsEidBroker));
    }

    [Fact]
    public void A_refusal_is_the_error_page_on_either_broker()
    {
        Assert.Equal(
            Disposition.ErrorPage,
            DispositionClassifier.Classify(
                Redirect(302, "https://a.example.invalid/auth/open/Error?errorId=CfDJ8abc"),
                BrokerTarget.Signicat));
    }

    /// <summary>
    /// A broker that declares no login path classifies nothing as one.
    /// </summary>
    /// <remarks>
    /// This is the case worth having. Both brokers run IdentityServer, so guessing the second
    /// one's login path from the first is tempting and is the dangerous kind of nearly-right:
    /// the second's MitID journey is known to leave the tenant host entirely. Unclassified is the
    /// honest answer, and the run that produces it prints where the request actually went.
    /// </remarks>
    [Theory]
    [InlineData("https://preprod.signicat.com/std/method/dtp?target=x")]
    [InlineData("https://a.example.invalid/auth/open/Account/Login?ReturnUrl=x")]
    public void A_broker_with_no_declared_login_path_classifies_nothing_as_one(string location)
    {
        Assert.Equal(
            Disposition.Unclassified,
            DispositionClassifier.Classify(Redirect(302, location), BrokerTarget.Signicat));
    }

    /// <summary>
    /// An OAuth error object is classified by whether it says more than the code.
    /// </summary>
    /// <remarks>
    /// The bare form is a finding about the first broker rather than a fact about OAuth, and
    /// naming the disposition for it left the other shape - which the second broker is expected
    /// to send - falling through to Unclassified, where it would read as a surprise rather than
    /// as the answer.
    /// </remarks>
    [Theory]
    [InlineData("""{"error":"invalid_client"}""", Disposition.BareJson)]
    [InlineData("""{"error":"invalid_request_object","error_description":"Invalid JWT request"}""", Disposition.DescribedJson)]
    [InlineData("""{"error":"invalid_grant","error_uri":"https://example.invalid/help"}""", Disposition.DescribedJson)]
    public void An_oauth_error_is_named_for_how_much_it_says(string json, Disposition expected)
    {
        Assert.Equal(expected, DispositionClassifier.Classify(Body(400, json), BrokerTarget.NetsEidBroker));
    }

    /// <summary>
    /// Every recorded answer classifies as the case that recorded it says it should.
    /// </summary>
    /// <remarks>
    /// The one that matters. Moving two literals onto the target and splitting a disposition in
    /// two is exactly the change that reclassifies a recording quietly, and a reclassified
    /// recording is a fixture that now claims something different about the broker.
    /// <para>
    /// Against the catalog rather than against the committed <c>meta.json</c>, because a case's
    /// expectation is the live claim and the meta is a copy of it from whenever the case was last
    /// recorded. The two can drift, and where they have, it is the pack that is behind.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Recorded))]
    public void A_recorded_answer_is_called_what_its_case_expects(string id, string expected)
    {
        var head = File.ReadAllLines(Repository.Fixture(id, "response.head"));
        var status = int.Parse(head[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

        var headers = head.Skip(1)
            .Where(line => line.Contains(':', StringComparison.Ordinal))
            .Select(line => line.Split(':', 2))
            .Select(parts => new KeyValuePair<string, string>(parts[0].Trim(), parts[1].Trim()))
            .ToList();

        var exchange = new RecordedExchange(
            "GET", "https://example.invalid/", [], null, status, null, headers,
            File.ReadAllBytes(Repository.Fixture(id, "response.raw")));

        Assert.Equal(expected, DispositionClassifier.Classify(exchange, BrokerTarget.NetsEidBroker).ToString());
    }

    public static TheoryData<string, string> Recorded()
    {
        var data = new TheoryData<string, string>();
        foreach (var @case in CaptureCatalog.For(Broker.NetsEidBroker))
        {
            data.Add(@case.Id, @case.Expected.ToString());
        }

        return data;
    }
}
