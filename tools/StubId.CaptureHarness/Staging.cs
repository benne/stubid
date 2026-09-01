using System.Text.Json;
using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Holds the whole sitting in memory, and only writes fixtures once it is over.
/// </summary>
/// <remarks>
/// <para>
/// Writing as you go cannot scrub correctly. The values worth hiding are born during the
/// sitting — the authorization code, the session identifier, the subject, every token — and
/// they appear in exchanges recorded <em>before</em> the response that first names them.
/// Only once the sitting is complete is the full set known.
/// </para>
/// <para>
/// Replacement is per value, not per occurrence. Whether the session identifier in one token
/// equals the one in another is a question this sitting is paying an authentication to
/// answer, and a fresh pseudonym each time would destroy the answer while looking careful.
/// </para>
/// </remarks>
public sealed partial class Staging
{
    private readonly List<(ManualCase Case, string Name, RecordedExchange Exchange)> _staged = [];
    private readonly Dictionary<string, string> _discovered = new(StringComparer.Ordinal);
    private int _counter;

    public int Count => _staged.Count;

    public IReadOnlyList<string> Recorded => [.. _staged.Select(s => s.Case.Id).Distinct()];

    /// <summary>
    /// What has been captured so far, scrubbed, so it can be looked at mid-sitting. Nothing
    /// is written until the end, and a sitting that turns out to have recorded the wrong
    /// thing is much cheaper to notice now than afterwards.
    /// </summary>
    public IEnumerable<(string Case, string Exchange, int Status, string Body)> Preview() =>
        _staged.Select(s => (
            s.Case.Id,
            s.Name,
            s.Exchange.StatusCode,
            Scrub(System.Text.Encoding.UTF8.GetString(s.Exchange.ResponseBody))));

    public void Add(ManualCase @case, string name, RecordedExchange exchange)
    {
        // A step can call the same endpoint twice - redeeming a code and then replaying it -
        // and both would land in one directory, where the second silently overwrote the
        // first. Numbering keeps them apart and keeps the order visible.
        var taken = _staged.Count(s => s.Case.Id == @case.Id && s.Name.StartsWith(name, StringComparison.Ordinal));

        _staged.Add((@case, taken == 0 ? name : $"{name}-{taken + 1}", exchange));
    }

    /// <summary>
    /// Registers a value born during the sitting, so every appearance of it becomes the same
    /// placeholder wherever it turns up.
    /// </summary>
    public string Discover(string kind, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8)
        {
            return value;
        }

        if (!_discovered.TryGetValue(value, out var placeholder))
        {
            placeholder = $"{{{{{kind.ToUpperInvariant()}_{++_counter}}}}}";
            _discovered[value] = placeholder;
        }

        return placeholder;
    }

    /// <summary>Finds the values worth hiding in a JSON body and registers each one.</summary>
    public void DiscoverIn(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var kind = member.Name switch
                {
                    "access_token" or "refresh_token" => "TOKEN",
                    "sub" or "sid" or "neb_sid" or "session_identifier" => member.Name.ToUpperInvariant(),
                    "code" => "CODE",
                    _ => null,
                };

                if (kind is not null)
                {
                    Discover(kind, member.Value.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON. The regular scrubbing still applies.
        }
    }

    /// <summary>
    /// Writes every staged exchange, scrubbed against the complete set of discovered values.
    /// </summary>
    public async Task<int> WriteAsync(FixtureStore store, CancellationToken ct)
    {
        var written = 0;

        foreach (var (@case, name, exchange) in _staged)
        {
            var directory = Path.Combine(store.Root, @case.Id, name);
            Directory.CreateDirectory(directory);

            var body = System.Text.Encoding.UTF8.GetString(exchange.ResponseBody);
            var (withoutTokens, tokens) = TokenFixtures.Extract(body);

            foreach (var (member, token) in tokens)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"{member}.header.json"), Scrub(token.Header), ct);
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"{member}.payload.json"), Scrub(token.Payload), ct);
            }

            await File.WriteAllTextAsync(
                Path.Combine(directory, "response.raw"), Scrub(withoutTokens), ct);

            // A signed step's authorize URL carries a compact JWS of our own making, and it
            // must not reach a fixture: the guard rejects one, and one has arrived in a fixture
            // twice already. Same treatment as a token in a body - the placeholder holds the
            // position, the decoded halves are written beside it.
            var (url, requestObject) = RequestObject.StripFrom(exchange.Url);
            if (requestObject is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "request_object.header.json"),
                    Scrub(requestObject.Header), ct);
                await File.WriteAllTextAsync(Path.Combine(directory, "request_object.payload.json"),
                    Scrub(requestObject.Payload), ct);
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "response.head"),
                string.Join('\n', new[] { $"HTTP {exchange.StatusCode} {exchange.ReasonPhrase}" }
                    .Concat(exchange.ResponseHeaders.Select(h => $"{h.Key}: {Scrub(h.Value)}"))) + "\n", ct);

            await File.WriteAllTextAsync(Path.Combine(directory, "meta.json"), JsonSerializer.Serialize(new
            {
                id = @case.Id,
                step = @case.Step,
                title = @case.Title,
                settles = @case.Settles,
                exchange = name,
                request = new { method = exchange.Method, url = Scrub(url) },
                requestObject = requestObject is null
                    ? null
                    : (object)new { requestObject.Algorithm, requestObject.SegmentLengths },
                status = exchange.StatusCode,
                tokens = tokens.ToDictionary(t => t.Key, t => new
                {
                    t.Value.Algorithm,
                    t.Value.Kid,
                    t.Value.SegmentLengths,
                    t.Value.SignatureVerified,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);

            written++;
        }

        return written;
    }

    /// <summary>
    /// Applies the discovered values, then the configured credentials and redactions. Ordered
    /// longest first, so a value that contains another is replaced before its substring is.
    /// </summary>
    public string Scrub(string text)
    {
        foreach (var (value, placeholder) in _discovered.OrderByDescending(d => d.Key.Length))
        {
            text = text.Replace(value, placeholder, StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(value), placeholder, StringComparison.Ordinal);
        }

        return Scrubber.Scrub(text);
    }

    /// <summary>
    /// What is still unaccounted for once everything known has been replaced. Reported before
    /// anything is written, because a sitting is expensive and a surprise here is cheaper to
    /// look at now than after the fixtures land.
    /// </summary>
    public IReadOnlyList<string> Suspicious()
    {
        var found = new List<string>();

        foreach (var (@case, name, exchange) in _staged)
        {
            // Check what will be written, not what was staged. Checking the staged body
            // reported every token that was about to be extracted correctly — eleven false
            // alarms in one sitting — while missing the two that genuinely leaked, which is
            // how a safety net teaches people to walk around it.
            var (body, tokens) = TokenFixtures.Extract(
                System.Text.Encoding.UTF8.GetString(exchange.ResponseBody));

            var parts = new List<(string Where, string Text)> { ("body", Scrub(body)) };
            parts.AddRange(tokens.SelectMany(t => new[]
            {
                ($"{t.Key} header", Scrub(t.Value.Header)),
                ($"{t.Key} payload", Scrub(t.Value.Payload)),
            }));
            parts.AddRange(exchange.ResponseHeaders.Select(h => ($"header {h.Key}", Scrub(h.Value))));

            foreach (var (where, text) in parts)
            {
                var cpr = SensitiveContent.FindCpr(text);
                if (cpr.Found)
                {
                    found.Add($"{@case.Id}/{name}: a personal number in the {where}, {cpr.Location}");
                }

                var token = SensitiveContent.FindSignedToken(text);
                if (token.Found)
                {
                    found.Add($"{@case.Id}/{name}: an unextracted signed token in the {where}");
                }
            }
        }

        return found;
    }
}
