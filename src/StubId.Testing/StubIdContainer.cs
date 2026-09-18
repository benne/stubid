using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Containers;
using StubId.Client;

namespace StubId.Testing;

/// <summary>A StubID instance in Docker, and the control API over it.</summary>
public sealed class StubIdContainer : DockerContainer
{
    private readonly StubIdConfiguration _configuration;
    private readonly Lock _gate = new();
    private readonly string? _predicted;
    private StubIdClient? _control;
    private string? _reported;

    /// <summary>
    /// A container over a configuration <see cref="StubIdBuilder.Build" /> has validated.
    /// </summary>
    public StubIdContainer(StubIdConfiguration configuration)
        : base(configuration)
    {
        _configuration = configuration;

        // Read here rather than when the authority is asked for, so that a broker this module
        // cannot place is known to be one before anything has been started around it.
        _predicted = BrokerRoots.Of(configuration.Profile);
    }

    /// <summary>
    /// The address this instance says it answers at, which every issuer it emits is built from.
    /// </summary>
    /// <remarks>
    /// The same as <see cref="MappedAddress" /> unless the caller pinned one, in which case it is
    /// the pinned value - a name that means something to a browser or a sibling container and
    /// possibly nothing to this process.
    /// </remarks>
    public Uri BaseAddress => _configuration.PublicBaseUrl ?? MappedClientAddress;

    /// <summary>
    /// The certificate this instance serves TLS with, or null when it serves plain HTTP.
    /// </summary>
    /// <remarks>
    /// Read once during start, over the plain-HTTP control API, which is the only transport a caller
    /// can reach before it knows what to trust.
    /// </remarks>
    public X509Certificate2? ServerCertificate { get; internal set; }

    /// <summary>
    /// The path the emulated broker's surface sits under: composed from the broker that was
    /// chosen, and confirmed by the instance during start.
    /// </summary>
    /// <remarks>
    /// Both, deliberately. Where the surface sits is knowable with nothing to ask, and a suite
    /// that pinned its address reads the whole authority that way, before anything has started -
    /// the port Docker assigns is what the rest of it waits on. The running instance is still what
    /// decides the root, so what it reports is compared against what was composed rather than
    /// quietly replacing it. The table
    /// that makes the first half possible is kept honest by a test rather than by being the only
    /// answer; <see cref="BrokerRoots" /> says how.
    /// </remarks>
    internal string ProfileRoot => _reported ?? _predicted ?? throw NotPlaceable();

    /// <summary>What the running instance answered, checked against what was composed.</summary>
    /// <remarks>
    /// A disagreement is a failure rather than a correction, because by the time it can be
    /// detected the authority may already have been read and handed to a client library. The one
    /// way to reach it is to choose the broker with an environment variable, which the server sees
    /// and this module does not.
    /// <para>
    /// A null root is an instance too old to name one, and the field arrived before any second
    /// broker did - so such an image is taken to be serving the first broker and checked like any
    /// other answer. Leaving the composed root standing instead would let the caller's own word go
    /// unchallenged by the one thing that could challenge it.
    /// </para>
    /// </remarks>
    internal void AcceptRootFromInstance(string? root)
    {
        root ??= BrokerRoots.OfDefault;

        if (_predicted is { } predicted && !string.Equals(predicted, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This instance serves its surface under '/{root}', where the module composed an "
                + $"authority ending in '/{predicted}'. Either the broker was chosen by setting "
                + $"StubId__Profile directly, which reaches the server and not this module, or this "
                + $"image does not serve that broker there - one too old to name a root at all is "
                + $"read as serving {BrokerRoots.Default}. Choose the broker with WithProfile so "
                + $"both halves are told the same thing.");
        }

        _reported = root;
    }

    private InvalidOperationException NotPlaceable() => new(
        $"This module has no root for the broker '{_configuration.Profile}', so the authority "
        + $"cannot be composed until the instance has started and been asked for one. Read it after "
        + $"StartAsync, or name a broker this module knows: {string.Join(", ", BrokerRoots.Known)}.");

    /// <summary>Where this process reaches the container: the host and the mapped port.</summary>
    /// <remarks>
    /// Distinct from <see cref="BaseAddress" /> on purpose. A pinned instance is told to call itself
    /// something the test host may not resolve, and anything dialling it from here still has to use
    /// the port Docker actually published.
    /// </remarks>
    public Uri MappedAddress =>
        new UriBuilder(
            Uri.UriSchemeHttp, Hostname, GetMappedPublicPort(StubIdBuilder.StubIdPort)).Uri;

    /// <summary>
    /// Where a client library reaches the container: https when TLS is on, and the same as
    /// <see cref="MappedAddress" /> when it is not.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MappedAddress" /> on purpose. The control API keeps to plain HTTP so
    /// that creating a citizen never depends on a trust decision, while the address the instance
    /// publishes as its own - and therefore every issuer it emits - names the secured port.
    /// </remarks>
    internal Uri MappedClientAddress =>
        _configuration.Tls is true
            ? new UriBuilder(
                Uri.UriSchemeHttps, Hostname, GetMappedPublicPort(StubIdBuilder.StubIdTlsPort)).Uri
            : MappedAddress;

    /// <summary>
    /// What a client library is configured with. The issuer it then discovers equals this character
    /// for character, which is the comparison openid-client and Spring Security both make.
    /// </summary>
    /// <remarks>Not <see cref="Uri.Authority" />, which is a host and a port.</remarks>
    public Uri Authority =>
        ProfileRoot.Length == 0 ? BaseAddress : new Uri(BaseAddress, ProfileRoot);

    /// <summary>The control API, over this instance.</summary>
    /// <remarks>
    /// Reached at <see cref="MappedAddress" /> rather than <see cref="BaseAddress" />: a pinned
    /// instance answers to a name this process may have no route to, and the control API is for
    /// this process.
    /// <para>
    /// The citizens, sessions, behavior and clock groups have shortcuts on this type;
    /// <see cref="StubIdClient.Runtime" /> deliberately does not, and is reached here. It is also
    /// what the module drives while the container starts, so republishing the address through it
    /// moves the issuer out from under <see cref="BaseAddress" /> and <see cref="Authority" />,
    /// which is a thing to do on purpose rather than by reaching for the nearest shortcut.
    /// </para>
    /// </remarks>
    public StubIdClient Control
    {
        get
        {
            // Lazy because the address is not knowable until Docker has started the container.
            lock (_gate)
            {
                return _control ??= new StubIdClient(MappedAddress);
            }
        }
    }

    /// <summary>The people a login can resolve as, over <see cref="Control" />.</summary>
    public CitizenApi Citizens => Control.Citizens;

    /// <summary>The logins themselves, over <see cref="Control" />.</summary>
    public SessionApi Sessions => Control.Sessions;

    /// <summary>
    /// Outcomes queued ahead of the logins they resolve, over <see cref="Control" />.
    /// </summary>
    public BehaviorApi Behavior => Control.Behavior;

    /// <summary>
    /// The clock, over <see cref="Control" />. Readable always; movable when the instance was
    /// built with <see cref="StubIdBuilder.WithControllableClock" />.
    /// </summary>
    public ClockApi Time => Control.Time;

    /// <summary>
    /// Clears the protocol state: the sessions, anything queued, and everything issued. Citizens
    /// survive, so a suite builds its people once. What a suite calls between tests when it reuses
    /// one instance.
    /// </summary>
    public Task ResetAsync(CancellationToken ct = default) => Control.ResetAsync(ct);

    /// <summary>
    /// An HTTP handler that trusts this instance's certificate, and nothing else.
    /// </summary>
    /// <remarks>
    /// Not a handler that accepts any certificate. The difference matters because the usual shortcut
    /// - returning true from the validation callback - is a habit that outlives the test it was
    /// written for, and it is one copied line away from a production client that validates nothing.
    /// This one compares what was presented against the exact certificate this container generated.
    /// </remarks>
    public HttpClientHandler CreateTrustingHandler()
    {
        if (ServerCertificate is not { } expected)
        {
            throw new InvalidOperationException(
                "This instance serves plain HTTP, so there is no certificate to trust. "
                + "Build it with WithTls() if you meant to secure it.");
        }

        var expectedBytes = expected.RawData;

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null
                && CryptographicOperations.FixedTimeEquals(presented.RawData, expectedBytes),
        };
    }

    /// <summary>Disposes the control client before the container goes away.</summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        lock (_gate)
        {
            _control?.Dispose();
            _control = null;
        }

        await base.DisposeAsyncCore();
    }
}
