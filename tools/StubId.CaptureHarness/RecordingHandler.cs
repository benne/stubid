namespace StubId.CaptureHarness;

/// <summary>
/// Records every back-channel exchange passing through it.
/// </summary>
/// <remarks>
/// The interactive session drives a real login, and the interesting exchanges — the token
/// request, userinfo, end session — happen behind the browser. Recording them at the handler
/// keeps the bytes as they were, rather than as a client library chose to present them.
/// </remarks>
public sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly List<RecordedExchange> _exchanges = [];

    public IReadOnlyList<RecordedExchange> Exchanges => _exchanges;

    public void Clear() => _exchanges.Clear();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
            .ToList();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        _exchanges.Add(new RecordedExchange(
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            [.. request.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))],
            requestBody is null ? null : Scrubber.Scrub(requestBody),
            (int)response.StatusCode,
            response.ReasonPhrase,
            headers,
            body));

        return response;
    }
}
