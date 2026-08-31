using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace StubId.Testing;

/// <summary>What this module needs to know beyond what any container needs.</summary>
/// <remarks>
/// One member, because one decision depends on it: whether the module publishes the mapped address
/// after start or stands aside because the caller pinned one. Everything else the builder offers is
/// an environment variable or a mount, and recording those here as well would be a second source of
/// truth for values Docker already holds.
/// </remarks>
public sealed class StubIdConfiguration : ContainerConfiguration
{
    public StubIdConfiguration(Uri? publicBaseUrl = null, bool? tls = null)
    {
        PublicBaseUrl = publicBaseUrl;
        Tls = tls;
    }

    public StubIdConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    public StubIdConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
    }

    public StubIdConfiguration(StubIdConfiguration resourceConfiguration)
        : this(new StubIdConfiguration(), resourceConfiguration)
    {
    }

    public StubIdConfiguration(StubIdConfiguration oldValue, StubIdConfiguration newValue)
        : base(oldValue, newValue)
    {
        PublicBaseUrl = BuildConfiguration.Combine(oldValue.PublicBaseUrl, newValue.PublicBaseUrl);
        Tls = BuildConfiguration.Combine(oldValue.Tls, newValue.Tls);
    }

    /// <summary>
    /// The address the caller pinned, or null to publish the mapped one once Docker has assigned it.
    /// </summary>
    public Uri? PublicBaseUrl { get; }

    /// <summary>Whether the instance serves TLS, which decides which port the address names.</summary>
    public bool? Tls { get; }
}
