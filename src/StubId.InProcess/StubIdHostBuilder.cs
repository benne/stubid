using Microsoft.Extensions.Logging;

namespace StubId.InProcess;

/// <summary>Builds a StubID instance that runs inside this process.</summary>
/// <remarks>
/// The twin of <c>StubIdBuilder</c> in StubId.Testing, and deliberately shaped like it, so a suite
/// that starts with one and moves to the other rewrites the first two lines and nothing else. What
/// is absent is absent for a reason rather than for lack of time: there is no image to choose, no
/// port to map and no transport to secure.
/// </remarks>
public sealed class StubIdHostBuilder
{
    /// <summary>What an instance calls itself when the caller did not say.</summary>
    /// <remarks>
    /// A name that cannot resolve, on purpose. Nothing dials it - the back channel is the handler
    /// this module hands out and the front channel is its client - so the only time it is looked
    /// up is when a caller forgot to point their client library at the handler, and then the error
    /// should name the mistake rather than whatever else happens to answer on that machine.
    /// <c>.invalid</c> is reserved by RFC 2606 and resolves nowhere, offline included.
    /// <para>
    /// It is https because that is the whole claim: a client library needs no relaxation to talk
    /// to this, and its RequireHttpsMetadata check is on the metadata scheme.
    /// </para>
    /// <para>
    /// Not <c>stubid.invalid</c>, which reads as the obvious choice: that string is the placeholder
    /// the recorded discovery document carries, so serving it would substitute the host for itself
    /// and a document that had stopped being rewritten would look correct.
    /// </para>
    /// </remarks>
    public const string DefaultPublicBaseUrl = "https://stubid-inprocess.invalid";

    private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal);

    private Action<ILoggingBuilder>? _logging;

    /// <summary>The address this instance answers at, which every issuer it emits is built from.</summary>
    /// <remarks>
    /// A container cannot know this before Docker assigns its port, so its module publishes the
    /// address after start. Here the caller chose it, so it is known before anything runs and a
    /// relying party can be configured against an instance that does not exist yet.
    /// </remarks>
    public StubIdHostBuilder WithPublicBaseUrl(Uri publicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(publicBaseUrl);

        _settings["StubId:PublicBaseUrl"] = publicBaseUrl.ToString().TrimEnd('/');

        return this;
    }

    /// <summary>A clock a test can move, so a five-minute timeout is reached in milliseconds.</summary>
    /// <remarks>Without it, moving the clock over the control API refuses rather than pretending.</remarks>
    public StubIdHostBuilder WithControllableClock(bool controllable = true)
    {
        _settings["StubId:ControllableClock"] = controllable ? "true" : "false";

        return this;
    }

    /// <summary>Whether a login that nothing else decided is approved.</summary>
    /// <remarks>False parks it, which is what an instance somebody is watching wants.</remarks>
    public StubIdHostBuilder WithAutomaticApproval(bool automatic)
    {
        _settings["StubId:ApproveAutomatically"] = automatic ? "true" : "false";

        return this;
    }

    /// <summary>Where the signing keys are written and read.</summary>
    /// <remarks>
    /// The default is one directory per machine, shared by every instance on it, for the same
    /// reason the container mounts a volume: keys that are regenerated fail every client that
    /// cached the metadata. Point this somewhere of its own for a test that wants to watch a key
    /// set change, and delete the directory afterwards.
    /// </remarks>
    public StubIdHostBuilder WithKeyPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _settings["StubId:KeyPath"] = path;

        return this;
    }

    /// <summary>Anything the typed surface above does not cover yet.</summary>
    /// <exception cref="ArgumentException">The key asks for TLS, which this host cannot serve.</exception>
    public StubIdHostBuilder WithSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        // Refused rather than passed through. Setting it would have the instance generate a
        // certificate onto disk that nothing serves, and report over the control API that it
        // serves TLS - a lie a caller would only discover from a connection that never happens.
        if (key.StartsWith("StubId:Tls", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An in-process host answers on an in-memory transport, so there is no listener to "
                + "secure and no certificate to present. Its authority is https regardless, which "
                + "is what lets a client library keep its metadata check on. For a host something "
                + "dials over TLS, use StubId.Testing's WithTls().",
                nameof(key));
        }

        _settings[key] = value;

        return this;
    }

    /// <summary>Where the instance's own logging goes. Silent unless asked.</summary>
    /// <remarks>
    /// The in-process twin of reading a container's logs, and off by default for the reason the
    /// container's are not written to your terminal either: a host that logs every request at
    /// information level puts its own noise into somebody else's test output.
    /// </remarks>
    public StubIdHostBuilder WithLogging(Action<ILoggingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _logging = configure;

        return this;
    }

    /// <summary>Captures the settings. Nothing is built and nothing is touched until start.</summary>
    public StubIdHost Build()
    {
        // Every setting the instance reads is materialised here, with its default, and not only
        // the ones the caller named. The alternative leaves them to be read from the environment,
        // and a StubId__ApproveAutomatically left over from somebody's compose stack would then
        // quietly decide their logins differently in process than in CI.
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["StubId:PublicBaseUrl"] = DefaultPublicBaseUrl,
            ["StubId:ControllableClock"] = "false",
            ["StubId:ApproveAutomatically"] = "true",
            ["StubId:KeyPath"] = Path.Combine(Path.GetTempPath(), "stubid-keys"),
            ["StubId:Tls"] = "",
        };

        foreach (var (key, value) in _settings)
        {
            settings[key] = value;
        }

        return new StubIdHost(settings, _logging);
    }
}
