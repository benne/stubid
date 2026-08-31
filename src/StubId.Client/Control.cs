using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace StubId.Client;

/// <summary>The one place a refusal becomes an exception, so every call refuses the same way.</summary>
internal static class Control
{
    internal static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var fault = await ReadFaultAsync(response, ct);

        throw new StubIdException(response.StatusCode, fault?.Error, fault?.Detail);
    }

    internal static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> shape, CancellationToken ct)
    {
        await EnsureAsync(response, ct);

        return await response.Content.ReadFromJsonAsync(shape, ct)
            ?? throw new StubIdException(response.StatusCode, "StubID answered with no body", null);
    }

    /// <summary>
    /// The error object, when there is one. A refusal that carries no JSON is still a refusal, so
    /// this answers null rather than throwing over the body of a message about a failure.
    /// </summary>
    internal static async Task<FaultBody?> ReadFaultAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(ControlJson.Default.FaultBody, ct);
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
