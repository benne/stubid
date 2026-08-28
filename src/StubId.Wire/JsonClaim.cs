using System.Text.Json;

namespace StubId.Wire;

/// <summary>
/// One claim, carrying its JSON representation rather than a CLR value.
/// </summary>
/// <remarks>
/// <para>
/// The type of a claim is part of the contract. The broker returns <c>"mitid.age":"35"</c>
/// and <c>"mitid.has_cpr":"true"</c> as JSON strings, and a client that parses them as
/// numbers or booleans breaks. Holding the raw JSON means nothing between here and the wire
/// gets an opinion about what a value ought to be.
/// </para>
/// <para>
/// Order matters too, which is why claims travel as a list rather than a dictionary.
/// </para>
/// </remarks>
public readonly record struct JsonClaim(string Name, string RawJson)
{
    public static JsonClaim String(string name, string value) =>
        new(name, JsonSerializer.Serialize(value));

    public static JsonClaim Number(string name, long value) =>
        new(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static JsonClaim Boolean(string name, bool value) =>
        new(name, value ? "true" : "false");

    /// <summary>
    /// A claim whose value is written verbatim. Use for arrays and objects, and for the
    /// cases where a broker types something unexpectedly.
    /// </summary>
    public static JsonClaim Raw(string name, string rawJson) => new(name, rawJson);

    public static JsonClaim Strings(string name, params string[] values) =>
        new(name, JsonSerializer.Serialize(values));
}
