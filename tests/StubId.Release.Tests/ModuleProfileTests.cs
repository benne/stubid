using StubId.Server;
using StubId.Testing;

namespace StubId.Release.Tests;

/// <summary>
/// The Testcontainers module says where a broker serves; the server decides it.
/// </summary>
/// <remarks>
/// The in-process package asks the server, because it runs it. The module runs an image instead and
/// cannot reference the assembly that knows, so it carries the roots itself - and a copy kept
/// somewhere else is right until the day a profile moves. This is what makes that day fail here
/// rather than in somebody's suite, and it is the whole reason the module is allowed to answer
/// before an instance exists.
/// </remarks>
public class ModuleProfileTests
{
    public static TheoryData<string> Brokers() => [.. BrokerProfiles.Available];

    /// <summary>Neither list has a broker the other does not.</summary>
    /// <remarks>
    /// A broker added to the server and not to the module would be served correctly and reported
    /// with no authority until it started, which is a worse failure than it sounds: the message
    /// blames the module's age, and the module is this build.
    /// </remarks>
    [Fact]
    public void The_module_knows_every_broker_this_build_serves_and_no_others()
    {
        Assert.Equal(
            BrokerProfiles.Available.Order(StringComparer.Ordinal),
            BrokerRoots.Known.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Brokers))]
    public void The_root_the_module_composes_is_the_one_the_profile_serves(string broker)
    {
        Assert.Equal(BrokerProfiles.Select(broker).Root.Segments, BrokerRoots.Of(broker));
    }

    /// <summary>And an instance nobody chose a broker for is the same broker on both sides.</summary>
    [Fact]
    public void The_module_and_the_server_serve_the_same_broker_by_default()
    {
        Assert.Equal(BrokerProfiles.Default, BrokerRoots.Default);
        Assert.Equal(BrokerProfiles.Select((string?)null).Root.Segments, BrokerRoots.Of(null));
    }
}
