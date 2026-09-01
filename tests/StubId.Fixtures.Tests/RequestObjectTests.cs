using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The signed request the harness sends, and the promise that none of it reaches a fixture.
/// </summary>
/// <remarks>
/// What the broker accepts was measured, not assumed - docs/research/signed-requests.md - and
/// these are the parts of that measurement the code has to keep true.
/// </remarks>
public class RequestObjectTests
{
    // Excluded by the credential guard's own negative lookahead, and useless besides.
    private const string Password = "not-a-real-secret";

    private const string Authority = "https://pp.netseidbroker.dk/op";
    private const string Client = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private static Dictionary<string, string> Parameters() => new(StringComparer.Ordinal)
    {
        ["client_id"] = Client,
        ["response_type"] = "code",
        ["redirect_uri"] = "http://localhost:5099/callback",
        ["scope"] = "openid mitid transaction_token",
        ["nonce"] = "a-nonce",
    };

    private static JsonElement Payload(string compact)
    {
        var segments = compact.Split('.');
        return JsonDocument
            .Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[1])))
            .RootElement.Clone();
    }

    [Fact]
    public void The_signature_verifies_against_the_client_secret()
    {
        var compact = RequestObject.Build(Parameters(), Client, Authority, Password);
        var segments = compact.Split('.');

        Assert.Equal(3, segments.Length);

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Password),
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"));

        Assert.Equal(Base64Url.EncodeToString(expected), segments[2]);
    }

    /// <remarks>
    /// The expensive one. A request object without exp is refused with bytes identical to a
    /// forged signature, so losing this line would not look like losing this line - it would
    /// look like the broker having stopped accepting signed requests at all.
    /// </remarks>
    [Fact]
    public void An_expiry_is_always_carried()
    {
        var payload = Payload(RequestObject.Build(Parameters(), Client, Authority, Password));

        Assert.True(payload.TryGetProperty("exp", out var exp));
        Assert.True(exp.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void Every_parameter_becomes_a_claim_and_the_issuer_is_the_client()
    {
        var parameters = Parameters();
        var payload = Payload(RequestObject.Build(parameters, Client, Authority, Password));

        foreach (var (key, value) in parameters)
        {
            Assert.Equal(value, payload.GetProperty(key).GetString());
        }

        Assert.Equal(Client, payload.GetProperty("iss").GetString());
        Assert.Equal(Authority, payload.GetProperty("aud").GetString());
    }

    [Fact]
    public void A_signed_step_sends_only_what_identifies_the_request()
    {
        var url = WithSecret(() => Session.BuildAuthorize(new ManualCase
        {
            Id = "CAP-TEST",
            Step = "Step 0",
            Title = "A signed step",
            Settles = "Nothing; this one is a test.",
            Operator = "Nothing.",
            Client = ClientProfile.OpenCode,
            SignRequest = true,
        }).Url);

        Assert.Equal(
            ["client_id", "request", "response_type"],
            Query(url).Keys.OrderBy(k => k, StringComparer.Ordinal));

        // The parameters are not lost, they have moved. That the broker reads them from in
        // there rather than from the query is the measured part.
        var payload = Payload(Uri.UnescapeDataString(Query(url)["request"]));
        foreach (var claim in new[] { "scope", "redirect_uri", "nonce", "code_challenge" })
        {
            Assert.True(payload.TryGetProperty(claim, out _), $"{claim} is missing from the object");
        }
    }

    [Fact]
    public void An_unsigned_step_is_left_exactly_as_it_was()
    {
        var (url, _, _) = Session.BuildAuthorize(new ManualCase
        {
            Id = "CAP-TEST",
            Step = "Step 0",
            Title = "An ordinary step",
            Settles = "Nothing; this one is a test.",
            Operator = "Nothing.",
            Client = ClientProfile.OpenCode,
        });

        Assert.DoesNotContain("request=", url, StringComparison.Ordinal);
        Assert.Contains("scope=", url, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The one that ties this to the guard it could otherwise defeat. A compact JWS has
    /// reached a fixture twice in this project's history; a signed step would be the third
    /// way in.
    /// </remarks>
    [Fact]
    public void A_recorded_request_object_leaves_no_token_in_the_url()
    {
        var compact = RequestObject.Build(Parameters(), Client, Authority, Password);
        var sent = $"{Authority}/connect/authorize?client_id={Client}&response_type=code"
                   + $"&request={Uri.EscapeDataString(compact)}";

        Assert.True(SensitiveContent.FindSignedToken(sent).Found, "the sample is not a token");

        var (stripped, extracted) = RequestObject.StripFrom(sent);

        Assert.False(SensitiveContent.FindSignedToken(stripped).Found);
        Assert.Contains(RequestObject.Placeholder, stripped, StringComparison.Ordinal);
        Assert.NotNull(extracted);
        Assert.Equal("HS256", extracted.Algorithm);
        Assert.Contains("\"scope\"", extracted.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_without_a_request_object_is_untouched()
    {
        var url = $"{Authority}/connect/authorize?client_id={Client}&scope=openid";
        var (stripped, extracted) = RequestObject.StripFrom(url);

        Assert.Equal(url, stripped);
        Assert.Null(extracted);
    }

    /// <summary>
    /// The environment wins over capture.local.json, so this is deterministic on a machine
    /// that has real credentials and on one that has none, which is what CI is.
    /// </summary>
    private static T WithSecret<T>(Func<T> act)
    {
        const string Name = "STUBID_NEB_PP_CODE_CLIENT_SECRET";
        var previous = Environment.GetEnvironmentVariable(Name);
        Environment.SetEnvironmentVariable(Name, Password);
        try
        {
            return act();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, previous);
        }
    }

    private static Dictionary<string, string> Query(string url) => new Uri(url).Query
        .TrimStart('?')
        .Split('&')
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.Ordinal);
}
