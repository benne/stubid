using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StubId.CaptureHarness;

/// <summary>
/// Reads and writes fixture directories. One directory per capture case:
/// request.json, response.head, response.raw and meta.json, with a MANIFEST.json at the
/// root hashing every file so a silent edit fails the build.
/// </summary>
public sealed class FixtureStore(string root)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; } = root;

    public string DirectoryFor(CaptureCase @case) => Path.Combine(Root, @case.Id);

    public async Task WriteAsync(CaptureCase @case, RecordedExchange exchange, CancellationToken ct)
    {
        var dir = DirectoryFor(@case);
        Directory.CreateDirectory(dir);

        var request = new
        {
            method = exchange.Method,
            url = Scrubber.Scrub(exchange.Url),
            headers = exchange.RequestHeaders.Select(h => new
            {
                name = h.Key,
                // An Authorization header carries a bearer token, or base64 of id:secret
                // which no value-based replacement can see. Neither is contract, so the
                // name is kept and the value is not.
                value = IsCredentialHeader(h.Key) ? "<redacted>" : Scrubber.Scrub(h.Value),
            }),
            body = exchange.RequestBody,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "request.json"), JsonSerializer.Serialize(request, Json) + "\n", ct);

        var head = new StringBuilder();
        head.Append("HTTP ").Append(exchange.StatusCode).Append(' ')
            .AppendLine(exchange.ReasonPhrase ?? "");
        foreach (var (name, value) in exchange.ResponseHeaders)
        {
            // A session cookie is a credential until it expires, not an identifier. The
            // contract is the cookie's name and flags, so those are kept and the value is
            // replaced with one of the same length.
            head.Append(name).Append(": ").AppendLine(HeaderValue(name, value, Scrubber.Scrub));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "response.head"), head.ToString(), ct);

        await File.WriteAllBytesAsync(
            Path.Combine(dir, "response.raw"), ScrubBody(exchange.ResponseBody), ct);

        var meta = new
        {
            id = @case.Id,
            description = @case.Description,
            settles = @case.Settles,
            disposition = @case.Expected.ToString(),
            status = exchange.StatusCode,
            contentType = exchange.Header("Content-Type"),
            byteLength = exchange.ResponseBody.Length,
            volatileHeaders = @case.VolatileHeaders,
            volatileBodyPatterns = @case.VolatileBodyPatterns,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, Json) + "\n", ct);
    }

    private static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A response header as a fixture records it: a cookie keeps its name and every attribute
    /// and loses its value, everything else is scrubbed.
    /// </summary>
    /// <remarks>
    /// Shared, because there are two writers. This one records the unattended pack; Staging
    /// records the sitting, and it wrote the served cookie value for every exchange of the
    /// first one - which is the path that matters, since the sitting is the pack with an
    /// authenticated session behind it.
    /// </remarks>
    public static string HeaderValue(string name, string value, Func<string, string> scrub) =>
        name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            ? MaskCookieValue(value)
            : scrub(value);

    /// <summary>
    /// Replaces a cookie's value while keeping its name and every attribute after it. The
    /// replacement is the same length as the original so a recorded Content-Length or an
    /// assertion about header size stays true.
    /// </summary>
    private static string MaskCookieValue(string setCookie)
    {
        var equals = setCookie.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return setCookie;
        }

        var semicolon = setCookie.IndexOf(';', equals);
        var end = semicolon < 0 ? setCookie.Length : semicolon;
        var length = end - equals - 1;

        return string.Concat(
            setCookie.AsSpan(0, equals + 1),
            new string('x', length),
            setCookie.AsSpan(end));
    }

    /// <summary>
    /// Scrubs a body only when it survives a UTF-8 round trip unchanged. A response that is
    /// not valid UTF-8 is written through untouched rather than corrupted, since the bytes as
    /// served are the point.
    /// </summary>
    private static byte[] ScrubBody(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        if (!Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(body))
        {
            return body;
        }

        var scrubbed = Scrubber.Scrub(text);
        return ReferenceEquals(scrubbed, text) || scrubbed == text
            ? body
            : Encoding.UTF8.GetBytes(scrubbed);
    }

    /// <summary>When the committed pack says it was recorded, if there is one.</summary>
    public async Task<string?> CapturedAtAsync(CancellationToken ct)
    {
        var path = Path.Combine(Root, "MANIFEST.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct));
        return document.RootElement.TryGetProperty("capturedAtUtc", out var value)
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Rewrites the manifest without changing when the pack says it was recorded.
    /// </summary>
    /// <remarks>
    /// Nothing that rewrites part of a pack may restamp it. The date says when the recordings
    /// were made, and a sitting records some of the steps rather than all of them - one that
    /// adds a single case must not claim the eleven beside it were recorded today. Each
    /// exchange's own date is in its response.head either way, and a pack with no manifest is
    /// being written for the first time, so it stamps now.
    /// </remarks>
    public async Task WriteManifestKeepingDateAsync(CancellationToken ct) =>
        await WriteManifestAsync(await CapturedAtAsync(ct) ?? Now(), ct);

    /// <summary>Now, in the format the manifest records.</summary>
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public async Task WriteManifestAsync(string capturedAtUtc, CancellationToken ct)
    {
        var files = Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "MANIFEST.json")
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in files)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Root, relative), ct);
            entries[relative] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        var manifest = new { capturedAtUtc, files = entries };
        await File.WriteAllTextAsync(
            Path.Combine(Root, "MANIFEST.json"), JsonSerializer.Serialize(manifest, Json) + "\n", ct);
    }
}
