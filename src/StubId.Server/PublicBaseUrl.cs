namespace StubId.Server;

/// <summary>
/// The address a client is configured with, which every issuer and every absolute URL StubID
/// emits is built from.
/// </summary>
/// <remarks>
/// Mutable, and seeded rather than fixed, because the value cannot always be known when the
/// process starts: a container does not learn its own mapped host port until Docker has started
/// it, and the test that mapped it is the only party that knows. Deriving it from the request
/// instead - the Host header, forwarded headers, anything - is the failure this type exists to
/// prevent. A client library compares the issuer it discovers against the authority it was
/// configured with character for character, so an issuer that follows the request is right for
/// one caller and wrong for the next, and the symptom arrives later as a key-resolution error
/// with nothing on the client's side to explain it.
/// </remarks>
public sealed class PublicBaseUrl
{
    /// <summary>Said the same way wherever an unset address is reported.</summary>
    public const string NotSetDetail =
        "Start with StubId__PublicBaseUrl=http://host:port, or PUT it to "
        + "/_stubid/v1/runtime/public-base-url.";

    // Reference assignment is atomic and volatile gives every reader the latest write, which is
    // the whole of the concurrency story here: one writer, many readers, no compound state.
    private volatile string? _value;

    /// <summary>
    /// Seeds the address from configuration, and refuses to start on a value that would produce
    /// a wrong issuer.
    /// </summary>
    /// <remarks>
    /// Discarding a bad configuration value would leave the instance answering 503 with a detail
    /// telling the operator to set the very key they did set. Failing here says which part of it
    /// was wrong, once, at the moment they can still read the output.
    /// </remarks>
    public PublicBaseUrl(IConfiguration configuration)
    {
        var configured = configuration["StubId:PublicBaseUrl"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            _value = null;
            return;
        }

        if (!TryNormalise(configured, out var seeded, out var fault))
        {
            throw new InvalidOperationException(
                $"StubId:PublicBaseUrl is not usable: {fault.Error}. {fault.Detail}");
        }

        _value = seeded;
    }

    /// <summary>The address, or null when nothing has set one.</summary>
    public string? Value => _value;

    /// <summary>Whether anything has set an address yet.</summary>
    public bool IsSet => _value is not null;

    /// <summary>
    /// Replaces the address. Last write wins: configuration seeds this value, it does not lock
    /// it, because the case the setter exists for is the one where the correct value could not
    /// be known when the process started.
    /// </summary>
    public void Set(string normalised) => _value = normalised;

    /// <summary>
    /// Whether a candidate can serve as the base of an issuer, and the exact string to store.
    /// </summary>
    /// <remarks>
    /// Trailing slashes are trimmed and nothing else is touched. The value is emitted verbatim,
    /// so rebuilding it from a parsed <see cref="Uri" /> would quietly drop a default port a
    /// caller had configured its client with, and the comparison that then fails is the one this
    /// type exists to keep true. The host is not resolved and the port is not probed; https is
    /// accepted even though StubID serves plain HTTP, because a proxy in front of it is a
    /// deployment the compose sample already documents.
    /// </remarks>
    public static bool TryNormalise(
        string? candidate,
        out string normalised,
        out (string Error, string Detail) fault)
    {
        normalised = "";

        if (string.IsNullOrWhiteSpace(candidate))
        {
            fault = ("a public base URL is required", """Send {"publicBaseUrl":"http://host:port"}.""");
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            fault = ("the public base URL must be absolute",
                "It needs a scheme and a host, like http://localhost:18080.");
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            fault = ("the public base URL must be http or https",
                $"A client cannot discover a document over '{parsed.Scheme}'.");
            return false;
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            fault = ("the public base URL must not carry a query or a fragment",
                "It is the base of every URL StubID emits, not a request.");
            return false;
        }

        var path = parsed.AbsolutePath.Trim('/');

        if (path.Length > 0)
        {
            // The mistake an operator actually makes is pasting the authority out of their own
            // client configuration, which already carries the segment we are about to add.
            fault = path.Equals("op", StringComparison.OrdinalIgnoreCase)
                ? ("the public base URL must not include the /op path segment",
                    "The issuer is this value plus /op. Send http://host:port.")
                : ("the public base URL must not carry a path",
                    "StubID serves the broker at the host root, under /op.");

            return false;
        }

        normalised = candidate.TrimEnd('/');
        fault = default;
        return true;
    }
}

/// <summary>Raised when something needs the address and nothing has set one.</summary>
public sealed class PublicBaseUrlNotSetException()
    : InvalidOperationException("The public base URL is not set. " + PublicBaseUrl.NotSetDetail);
