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
/// <param name="jwks">
/// The broker's key set as it stood when the sitting ran, used to check each recorded
/// signature while there is still a published key to check it against. Null records the
/// signatures as unchecked, which is what a sitting with no key set honestly has.
/// </param>
public sealed partial class Staging(string? jwks = null)
{
    private readonly List<(ManualCase Case, string Name, RecordedExchange Exchange, string? Note,
        string CapturedAtUtc)> _staged = [];
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

    /// <param name="note">
    /// Something about how this exchange was recorded that the bytes do not say. It reaches
    /// meta.json, because the alternative is that it reaches nobody.
    /// </param>
    public void Add(ManualCase @case, string name, RecordedExchange exchange, string? note = null)
    {
        // A step can call the same endpoint twice - redeeming a code and then replaying it -
        // and both would land in one directory, where the second silently overwrote the
        // first. Numbering keeps them apart and keeps the order visible.
        var taken = _staged.Count(s => s.Case.Id == @case.Id && s.Name.StartsWith(name, StringComparison.Ordinal));

        // Stamped here rather than at the write. /finish can be an hour after the exchange it
        // is writing, and the question a date on a recording answers is when the broker said
        // this, not when somebody got round to saving it.
        _staged.Add((@case, taken == 0 ? name : $"{name}-{taken + 1}", exchange, note,
            FixtureStore.Now()));
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

        foreach (var (@case, name, exchange, note, capturedAtUtc) in _staged)
        {
            var directory = Path.Combine(store.Root, @case.Id, name);
            Directory.CreateDirectory(directory);

            var body = System.Text.Encoding.UTF8.GetString(exchange.ResponseBody);

            // Checked here and nowhere later. Once the broker rotates a key, whether these
            // bytes verified against the published one is unanswerable - and the
            // transaction-signing key has rotated once already.
            var (withoutTokens, tokens) = TokenFixtures.Extract(
                body, jwks is null ? null : compact => TokenFixtures.Verify(compact, jwks));

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

                    // Which published certificate the kid resolved to, so the fixture says
                    // which key signed which token rather than leaving a thumbprint to be
                    // looked up against a key set that will have moved on.
                    Certificate = jwks is null ? null : TokenFixtures.SubjectFor(t.Value.Kid, jwks),
                    t.Value.SegmentLengths,
                    t.Value.SignatureVerified,
                }),

                // The pack keeps the date it already carries, so a later sitting does not
                // restamp recordings it did not make. That leaves nowhere for this exchange's
                // own date except here - the broker's Date header is on some of these responses
                // and not others, and a recorded callback has no response headers at all.
                capturedAtUtc,
                note = note is null ? null : Scrub(note),
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

        foreach (var (@case, name, exchange, _, _) in _staged)
        {
            // Check what will be written, not what was staged. Checking the staged body
            // reported every token that was about to be extracted correctly — eleven false
            // alarms in one sitting — while missing the two that genuinely leaked, which is
            // how a safety net teaches people to walk around it.
            var (body, tokens) = TokenFixtures.Extract(
                System.Text.Encoding.UTF8.GetString(exchange.ResponseBody));

            // The URL as it will be written, not as it was sent. A signed step's authorize URL
            // carries a compact JWS of our own making, which StripFrom removes on the way to
            // meta.json - and if its pattern ever misses a form of the request parameter, the
            // token lands in a fixture with nothing said here and only the next build to catch
            // it, by which time the sitting is over and the staged bytes are gone.
            var (writtenUrl, _) = RequestObject.StripFrom(exchange.Url);

            var parts = new List<(string Where, string Text)>
            {
                ("body", Scrub(body)),
                ("request URL", Scrub(writtenUrl)),
            };
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
