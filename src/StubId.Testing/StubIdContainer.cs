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
    public Uri BaseAddress => _configuration.PublicBaseUrl ?? MappedAddress;

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
