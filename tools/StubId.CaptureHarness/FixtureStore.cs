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
            url = exchange.Url,
            headers = exchange.RequestHeaders.Select(h => new { name = h.Key, value = h.Value }),
            body = exchange.RequestBody,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "request.json"), JsonSerializer.Serialize(request, Json) + "\n", ct);

        var head = new StringBuilder();
        head.Append("HTTP ").Append(exchange.StatusCode).Append(' ')
            .AppendLine(exchange.ReasonPhrase ?? "");
        foreach (var (name, value) in exchange.ResponseHeaders)
        {
            head.Append(name).Append(": ").AppendLine(Scrubber.Scrub(value));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "response.head"), head.ToString(), ct);

        await File.WriteAllBytesAsync(Path.Combine(dir, "response.raw"), exchange.ResponseBody, ct);

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
