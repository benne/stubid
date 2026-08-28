namespace StubId.Abstractions;

/// <summary>
/// How closely a piece of StubID's output has to match the broker it emulates.
/// </summary>
/// <remarks>
/// The tier decides how the conformance differ treats a mismatch, so it is worth being
/// deliberate: everything a client library parses or validates is <see cref="Exact"/>,
/// and anything below that is a promise StubID is not making.
/// </remarks>
public enum FidelityTier
{
    /// <summary>
    /// Byte-for-byte, including member order and JSON types, and including anything the
    /// broker omits. A mismatch is a bug.
    /// </summary>
    Exact,

    /// <summary>
    /// The value differs but the shape does not: identifiers, timestamps, key material.
    /// The differ asserts a pattern rather than a value.
    /// </summary>
    Shape,

    /// <summary>
    /// Deliberately outside the contract: infrastructure headers, page markup, timing,
    /// TLS. StubID does not try to match these and tests must not assert on them.
    /// </summary>
    OutOfContract,
}
