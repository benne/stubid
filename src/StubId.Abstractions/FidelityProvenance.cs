namespace StubId.Abstractions;

/// <summary>
/// Where the knowledge behind an emulated behaviour came from.
/// </summary>
/// <remarks>
/// Vendor documentation has contradicted the live broker on three separate occasions, so
/// StubID tracks how it knows what it knows. Anything short of
/// <see cref="VerifiedLive"/> is a candidate for being wrong.
/// </remarks>
public enum FidelityProvenance
{
    /// <summary>Recorded from the real broker. The fixture is the authority.</summary>
    VerifiedLive,

    /// <summary>Stated in vendor documentation and not contradicted anywhere.</summary>
    DocsConfirmed,

    /// <summary>
    /// Vendor sources disagree. StubID picks a default and offers the alternative behind a
    /// profile toggle until a recording settles it.
    /// </summary>
    DocsConflict,

    /// <summary>A reasonable guess. Undocumented and unrecorded.</summary>
    Assumed,

    /// <summary>Known to differ from the broker, on purpose. Requires a reason.</summary>
    Divergent,

    /// <summary>
    /// Advertised by the emulated surface but not implemented. Answers 501 with a link to
    /// the reason rather than a misleading 404. Requires a reason.
    /// </summary>
    NotEmulated,
}
