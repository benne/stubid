using System.Net;

namespace StubId.Client;

/// <summary>StubID refused, and the caller has something to change.</summary>
/// <remarks>
/// Not every refusal is one of these. A query for something that is not there returns absence, and
/// a decision that lost its race returns the outcome that won - both are answers, and turning them
/// into exceptions would make ordinary control flow read as failure. This is for the rest: a
/// citizen that does not exist, a clock that was not started controllable, an address that would
/// produce a wrong issuer.
/// </remarks>
public sealed class StubIdException : Exception
{
    internal StubIdException(HttpStatusCode statusCode, string? error, string? detail)
        : base(Describe(statusCode, error, detail))
    {
        StatusCode = statusCode;
        Error = error;
        Detail = detail;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>What StubID called it, in its own words.</summary>
    public string? Error { get; }

    /// <summary>What StubID said to do about it, in its own words.</summary>
    public string? Detail { get; }

    // The server's strings verbatim, with nothing added. It already says the useful thing - which
    // setting to change, which value was wrong - and paraphrasing it here would put two wordings
    // of one refusal in front of the same reader.
    private static string Describe(HttpStatusCode statusCode, string? error, string? detail) =>
        string.Join(
            ' ',
            new[] { $"StubID answered {(int)statusCode}{(error is null ? "." : $": {error}.")}", detail }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
}
