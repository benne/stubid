using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Volumes;
using StubId.Client;

namespace StubId.Testing;

/// <summary>Builds a StubID container for a test suite.</summary>
public sealed class StubIdBuilder : ContainerBuilder<StubIdBuilder, StubIdContainer, StubIdConfiguration>
{
    /// <summary>The image this module is tested against.</summary>
    public const string StubIdImage = "ghcr.io/benne/stubid:0.2.0";

    public const ushort StubIdPort = 8080;

    private const string ReadyPath = "/_stubid/health/ready";

    public StubIdBuilder()
        : this(new StubIdConfiguration()) => DockerResourceConfiguration = Init().DockerResourceConfiguration;

    public StubIdBuilder(string image)
        : this(new DockerImage(image))
    {
    }

    public StubIdBuilder(IImage image)
        : this(new StubIdConfiguration()) =>
        DockerResourceConfiguration = Init().WithImage(image).DockerResourceConfiguration;

    private StubIdBuilder(StubIdConfiguration resourceConfiguration)
        : base(resourceConfiguration) => DockerResourceConfiguration = resourceConfiguration;

    protected override StubIdConfiguration DockerResourceConfiguration { get; }

    /// <summary>
    /// Pins the address instead of letting the module publish the mapped one.
    /// </summary>
    /// <remarks>
    /// For when the browser and the application under test reach StubID by different names - a
    /// compose network, a proxy, a fixed host port - and both have to see one issuer. Without it the
    /// module publishes the address this process reaches the container at, which is what a suite
    /// driving the protocol itself wants.
    /// </remarks>
    public StubIdBuilder WithPublicBaseUrl(Uri publicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(publicBaseUrl);

        var address = publicBaseUrl.ToString().TrimEnd('/');

        return Merge(DockerResourceConfiguration, new StubIdConfiguration(new Uri(address)))
            .WithEnvironment("StubId__PublicBaseUrl", address);
    }

    /// <summary>
    /// A clock a test can move, so a five-minute timeout is reached in milliseconds.
    /// </summary>
    /// <remarks>
    /// Without this the instance reads the machine's clock and
    /// <see cref="ClockApi.AdvanceAsync" /> refuses.
    /// </remarks>
    public StubIdBuilder WithControllableClock(bool controllable = true) =>
        WithEnvironment("StubId__ControllableClock", controllable ? "true" : "false");

    /// <summary>
    /// Whether a login that nothing else decided is approved.
    /// </summary>
    /// <remarks>
    /// False parks it instead, which is what an instance somebody is watching wants - and what a
    /// test that never decides the login will wait out.
    /// </remarks>
    public StubIdBuilder WithAutomaticApproval(bool automatic) =>
        WithEnvironment("StubId__ApproveAutomatically", automatic ? "true" : "false");

    /// <summary>
    /// Keeps the signing keys in a named volume across containers.
    /// </summary>
    /// <remarks>
    /// Only meaningful with reuse: a container that is thrown away has nothing to carry forward, and
    /// each one already gets an anonymous volume that keeps its keys stable for its own lifetime.
    /// </remarks>
    public StubIdBuilder WithKeyVolume(string name) => WithVolumeMount(name, "/keys");

    public StubIdBuilder WithKeyVolume(IVolume volume) => WithVolumeMount(volume, "/keys");

    public override StubIdContainer Build()
    {
        Validate();

        return new StubIdContainer(DockerResourceConfiguration);
    }

    protected override StubIdBuilder Init() =>
        base.Init()
            .WithImage(StubIdImage)
            .WithPortBinding(StubIdPort, assignRandomHostPort: true)
            .WithStartupCallback(PublishTheMappedAddressAsync)

            // The interval and the timeout are both deliberate. The defaults are a one-second
            // interval and a one-hour timeout: the first throws away most of a sub-second start, and
            // the second turns a broken image into a stuck job rather than a failed test. Readiness
            // is HTTP because the runtime image is chiselled - there is no shell to exec into.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPath(ReadyPath).ForPort(StubIdPort),
                strategy => strategy
                    .WithInterval(TimeSpan.FromMilliseconds(100))
                    .WithTimeout(TimeSpan.FromSeconds(60))));

    protected override StubIdBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new StubIdConfiguration(resourceConfiguration));

    protected override StubIdBuilder Clone(IContainerConfiguration resourceConfiguration) =>
        Merge(DockerResourceConfiguration, new StubIdConfiguration(resourceConfiguration));

    protected override StubIdBuilder Merge(StubIdConfiguration oldValue, StubIdConfiguration newValue) =>
        new(new StubIdConfiguration(oldValue, newValue));

    /// <summary>
    /// Tells the instance the address this process reaches it at, before anything can discover a
    /// document with the wrong issuer in it.
    /// </summary>
    /// <remarks>
    /// Docker assigns the host port when it starts the container, so the correct issuer cannot be
    /// known any earlier than this. A startup callback runs after the port bindings are mapped and
    /// before the wait strategies, which is the only window where the port is known and nothing has
    /// been served. The application may not have bound its socket yet at that moment, so this waits
    /// for it rather than assuming the wait strategy already did - that strategy runs after this
    /// returns, and it is checking the readiness this call is about to create.
    /// </remarks>
    private static async Task PublishTheMappedAddressAsync(
        StubIdContainer container, StubIdConfiguration configuration, CancellationToken ct)
    {
        if (configuration.PublicBaseUrl is not null)
        {
            // Pinned by the caller. The environment variable already carried it into the process,
            // and replacing it with the mapped port is the mistake WithPublicBaseUrl exists to avoid.
            return;
        }

        var address = container.MappedAddress;

        using var control = new StubIdClient(address);

        try
        {
            await WaitForLivenessAsync(control, ct);
            await control.Runtime.SetPublicBaseUrlAsync(address, ct);
        }
        catch (Exception failure)
            when (failure is StubIdException or HttpRequestException or TaskCanceledException)
        {
            throw await StubIdContainerException.DescribeAsync(container, address, failure, ct);
        }
    }

    private static async Task WaitForLivenessAsync(StubIdClient control, CancellationToken ct)
    {
        var deadline = TimeSpan.FromSeconds(30);
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(50);

        while (!await control.IsLiveAsync(ct))
        {
            if (waited >= deadline)
            {
                throw new TimeoutException(
                    $"StubID did not answer on {control.Http.BaseAddress} within {deadline.TotalSeconds:0} seconds.");
            }

            await Task.Delay(step, ct);
            waited += step;
        }
    }
}
