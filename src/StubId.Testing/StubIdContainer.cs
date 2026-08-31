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
    private StubIdClient? _control;

    public StubIdContainer(StubIdConfiguration configuration)
        : base(configuration) => _configuration = configuration;

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
    public Uri Authority => new(BaseAddress, "op");

    /// <summary>The control API, over this instance.</summary>
    /// <remarks>
    /// Reached at <see cref="MappedAddress" /> rather than <see cref="BaseAddress" />: a pinned
    /// instance answers to a name this process may have no route to, and the control API is for
    /// this process.
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

    public CitizenApi Citizens => Control.Citizens;

    public SessionApi Sessions => Control.Sessions;

    public BehaviourApi Behaviour => Control.Behaviour;

    public ClockApi Time => Control.Time;

    /// <summary>
    /// Clears the sessions and anything queued, and keeps the citizens. What a suite calls between
    /// tests when it reuses one instance.
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
