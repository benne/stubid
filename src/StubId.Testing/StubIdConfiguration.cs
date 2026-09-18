using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace StubId.Testing;

/// <summary>What this module needs to know beyond what any container needs.</summary>
/// <remarks>
/// Only what the module itself decides something from: whether it publishes the mapped address
/// after start or stands aside because the caller pinned one, which port that address names, and
/// which broker was chosen - because the authority a caller reads before start is composed from
/// that broker's root. Everything else the builder offers is an environment variable or a mount,
/// and recording those here as well would be a second source of truth for values Docker holds.
/// </remarks>
public sealed class StubIdConfiguration : ContainerConfiguration
{
    /// <summary>
    /// What the builder records when a caller pins an address or asks for TLS, and the empty
    /// configuration everything else starts from.
    /// </summary>
    public StubIdConfiguration(Uri? publicBaseUrl = null, bool? tls = null)
    {
        PublicBaseUrl = publicBaseUrl;
        Tls = tls;
    }

    /// <summary>Carries the Docker resource settings over when the builder clones itself.</summary>
    public StubIdConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>Carries the container settings over when the builder clones itself.</summary>
    public StubIdConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    /// <summary>Copies one of these, by merging it onto an empty configuration.</summary>
    public StubIdConfiguration(StubIdConfiguration resourceConfiguration)
        : this(new StubIdConfiguration(), resourceConfiguration)
    {
    }

    /// <summary>
    /// Layers one configuration onto another, which is how each builder call adds to the last.
    /// </summary>
    public StubIdConfiguration(StubIdConfiguration oldValue, StubIdConfiguration newValue)
        : base(oldValue, newValue)
    {
        PublicBaseUrl = BuildConfiguration.Combine(oldValue.PublicBaseUrl, newValue.PublicBaseUrl);
        Tls = BuildConfiguration.Combine(oldValue.Tls, newValue.Tls);
        Profile = BuildConfiguration.Combine(oldValue.Profile, newValue.Profile);
    }

    /// <summary>
    /// The address the caller pinned, or null to publish the mapped one once Docker has assigned it.
    /// </summary>
    public Uri? PublicBaseUrl { get; }

    /// <summary>Whether the instance serves TLS, which decides which port the address names.</summary>
    public bool? Tls { get; }

    /// <summary>
    /// The broker the caller chose, or null when nothing did and the default is being served.
    /// </summary>
    /// <remarks>
    /// Recorded as well as sent to the instance, because <see cref="StubIdContainer.Authority" />
    /// ends in that broker's root and a caller reads it while there is still nothing to ask.
    /// <para>
    /// Set on its own rather than through the constructor above, which shipped taking two values
    /// and would retire that signature by gaining a third.
    /// </para>
    /// </remarks>
    public string? Profile { get; init; }
}
