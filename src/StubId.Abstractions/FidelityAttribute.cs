namespace StubId.Abstractions;

/// <summary>
/// Records what StubID knows about one piece of emulated behaviour, and how it knows it.
/// </summary>
/// <remarks>
/// <para>
/// One attribute, four consumers: the conformance differ reads the tier to decide how to
/// compare, the broker reference under <c>docs/brokers/</c> is generated from it, the
/// <c>/_stubid/v1/fidelity</c> endpoint serves it, and unimplemented endpoints use it to
/// answer 501 with a link to the reason. Adding a fifth mechanism instead of extending
/// this one is how documentation and behaviour drift apart.
/// </para>
/// <para>
/// Annotate as you build. Byte-faithful discovery advertises capabilities StubID does not
/// implement from its very first response, so divergences exist on day one and are much
/// more expensive to reconstruct later.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
///     Evidence = "fixtures/neb/pp/CAP-002")]
/// public JsonWebKeySet BuildJwks() { ... }
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum,
    AllowMultiple = true)]
public sealed class FidelityAttribute : Attribute
{
    public FidelityAttribute(FidelityTier tier, FidelityProvenance provenance)
    {
        Tier = tier;
        Provenance = provenance;
    }

    /// <summary>How closely this has to match the broker.</summary>
    public FidelityTier Tier { get; }

    /// <summary>Where the knowledge came from.</summary>
    public FidelityProvenance Provenance { get; }

    /// <summary>
    /// The fixture directory or source URL backing this. Required for
    /// <see cref="FidelityProvenance.VerifiedLive"/>, where the build checks the fixture
    /// exists.
    /// </summary>
    public string? Evidence { get; init; }

    /// <summary>
    /// Why StubID differs, or why it does not implement this. Required for
    /// <see cref="FidelityProvenance.Divergent"/> and
    /// <see cref="FidelityProvenance.NotEmulated"/>; it is what the 501 response links to.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The recording that would settle this, for anything not yet verified against the
    /// live broker. Tests for it stay skipped until that recording exists.
    /// </summary>
    public string? AwaitingCapture { get; init; }

    /// <summary>
    /// True when the attribute is missing something the build requires of it. Kept here
    /// rather than in the analyser so the rule has one home.
    /// </summary>
    public bool IsComplete => Provenance switch
    {
        FidelityProvenance.VerifiedLive => !string.IsNullOrWhiteSpace(Evidence),
        FidelityProvenance.Divergent or FidelityProvenance.NotEmulated
            => !string.IsNullOrWhiteSpace(Reason),
        FidelityProvenance.DocsConflict or FidelityProvenance.Assumed
            => !string.IsNullOrWhiteSpace(AwaitingCapture),
        _ => true,
    };
}
