using System.Net;

namespace StubId.CaptureHarness;

/// <summary>
/// Performs one capture case against the live broker.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly HttpClient _client;

    public Recorder()
    {
        var handler = new HttpClientHandler
        {
            // Redirects are the fact being recorded. Following them would throw away the
            // 302 to the broker's error page, which is how it refuses a bad request.
            AllowAutoRedirect = false,

            // Without this the body we record is a decoded view of what was served rather
            // than the bytes themselves.
            AutomaticDecompression = DecompressionMethods.None,

            UseCookies = false,
        };

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<RecordedExchange> RecordAsync(CaptureCase @case, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(@case.Method), @case.Url);
        var requestHeaders = new List<KeyValuePair<string, string>>();

        foreach (var (name, value) in @case.Headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
            requestHeaders.Add(new(name, value));
        }

        // Two bodies, built separately. Scrubbing the encoded form after the fact does not
        // work: percent-encoding hides the value from a plain string replace, which is how a
        // real secret ended up in a fixture the first time this ran.
        string? storedBody = null;
        if (@case.Form is not null)
        {
            static string Encode(IReadOnlyDictionary<string, string> form, Func<string, string> value) =>
                string.Join('&', form.Select(f =>
                    $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(value(f.Value))}"));

            storedBody = Encode(@case.Form, static v => v);
            var sentBody = Encode(@case.Form, Scrubber.Unscrub);

            request.Content = new StringContent(
                sentBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var response = await _client.SendAsync(request, ct);

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
            .ToList();

        var body = await response.Content.ReadAsByteArrayAsync(ct);

        return new RecordedExchange(
            @case.Method,
            @case.Url,
            requestHeaders,
            storedBody,
            (int)response.StatusCode,
            response.ReasonPhrase,
            responseHeaders,
            body);
    }

    public void Dispose() => _client.Dispose();
}
