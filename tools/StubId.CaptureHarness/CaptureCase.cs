namespace StubId.CaptureHarness;

/// <summary>
/// A single recording: what to send, and what question the result answers.
/// </summary>
public sealed class CaptureCase
{
    public required string Id { get; init; }

    /// <summary>One line, used as the fixture's title.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// What this recording settles. It ends up in the fixture's meta.json and in the
    /// generated broker reference, so that a year from now the reason a byte is asserted
    /// is written down next to the byte.
    /// </summary>
    public required string Settles { get; init; }

    public string Method { get; init; } = "GET";

    public required string Url { get; init; }

    /// <summary>Form fields for a POST. Encoded as application/x-www-form-urlencoded.</summary>
    public IReadOnlyDictionary<string, string>? Form { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Response headers that legitimately differ between two identical requests. They are
    /// still recorded, but the verify pass masks them before comparing.
    /// </summary>
    public IReadOnlyList<string> VolatileHeaders { get; init; } = [];

    /// <summary>
    /// Regular expressions matching parts of the response body that legitimately differ
    /// between runs. Each match is masked before comparing.
    /// </summary>
    public IReadOnlyList<string> VolatileBodyPatterns { get; init; } = [];

    /// <summary>
    /// How the broker is expected to answer. Recording a refusal is as much the point as
    /// recording a success: how a request is rejected is part of the contract, and a
    /// disposition that changes is a more useful alarm than a status code that changes.
    /// </summary>
    public required Disposition Expected { get; init; }
}
