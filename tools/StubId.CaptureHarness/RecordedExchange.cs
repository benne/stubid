namespace StubId.CaptureHarness;

/// <summary>
/// One request and its response, kept as close to the wire as HttpClient allows.
/// </summary>
/// <param name="Method">HTTP method as sent.</param>
/// <param name="Url">Absolute request URL as sent, before any redirect is followed.</param>
/// <param name="RequestHeaders">Headers we set explicitly. Transport headers are excluded.</param>
/// <param name="RequestBody">Request body as sent, or null.</param>
/// <param name="StatusCode">Numeric status.</param>
/// <param name="ReasonPhrase">Reason phrase as returned. HTTP/2 responses have none.</param>
/// <param name="ResponseHeaders">
/// Response headers in the order received, including duplicates. Order matters: it is part
/// of what a fixture pins.
/// </param>
/// <param name="ResponseBody">
/// Response body bytes exactly as served. The recorder disables decompression so this is
/// not a decoded view of something else.
/// </param>
public sealed record RecordedExchange(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    string? RequestBody,
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
    byte[] ResponseBody)
{
    public string? Header(string name) => ResponseHeaders
        .FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
        .Value;
}
