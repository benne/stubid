using System.Net;
using System.Text.RegularExpressions;

namespace StubId.Testing.Tests;

/// <summary>
/// The browser's share of driving a login: carrying cookies between requests, and reading back
/// the form the front channel posts.
/// </summary>
/// <remarks>
/// Shared because two suites here drive the same login against different relying parties - the
/// one <see cref="StockClientOverTlsTests" /> builds inline, and the sample application. Left
/// duplicated, the two would drift into simulating different browsers, and a failure in one
/// would stop meaning anything about the other.
/// </remarks>
internal static partial class Browser
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One request, with the jar's cookies on the way out and any new ones kept.</summary>
    internal static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        CookieJar cookies,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        cookies.ApplyTo(request);

        var response = await client.SendAsync(request, Ct);
        cookies.Capture(response);

        return response;
    }

    /// <summary>What a browser would submit from the self-posting form the front channel returns.</summary>
    internal static Dictionary<string, string> HiddenFields(string html) => HiddenField
        .Matches(html)
        .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));

    [GeneratedRegex("""<input type="hidden" name="([^"]+)" value="([^"]*)" />""")]
    private static partial Regex HiddenField { get; }
}

/// <summary>The cookies a browser would be holding part-way through a login.</summary>
internal sealed class CookieJar
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var pair in values.Select(v => v.Split(';')[0]))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = pair[..separator];
            var value = pair[(separator + 1)..];

            if (value.Length == 0)
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = value;
            }
        }
    }

    public void ApplyTo(HttpRequestMessage request)
    {
        if (_cookies.Count > 0)
        {
            request.Headers.Add(
                "Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }
    }
}
