namespace StubId.Server;

/// <summary>
/// Whether a login nothing else decided is approved on its own.
/// </summary>
/// <remarks>
/// The setting is <c>StubId:ApproveAutomatically</c>, and it is what an instance starts with. This
/// puts a switch in front of it, because the moment somebody is watching an instance is the moment
/// they want to stop it deciding for them - and restarting a container to change one boolean loses
/// the sessions they were looking at.
/// <para>
/// An override rather than a write to configuration. Configuration is what the instance was told
/// at startup and a test module has already written it; overwriting it would leave no way to say
/// "go back to what this instance was started with", which is what clearing the override does.
/// </para>
/// </remarks>
public sealed class AutomaticApproval(IConfiguration configuration)
{
    private volatile object? _override;

    /// <summary>What the instance was started with.</summary>
    public bool Configured =>
        configuration.GetValue("StubId:ApproveAutomatically", defaultValue: true);

    /// <summary>What somebody set while it was running, or null if nobody has.</summary>
    public bool? Overridden => (bool?)_override;

    /// <summary>What the ladder actually asks, which is the override where there is one.</summary>
    public bool Enabled => Overridden ?? Configured;

    /// <summary>Sets the override, or clears it with null.</summary>
    public void Set(bool? enabled) => _override = enabled;
}
