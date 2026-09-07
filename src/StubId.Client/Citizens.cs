namespace StubId.Client;

/// <summary>A person a login can be resolved as, with the properties MitID would carry.</summary>
public sealed record StubIdCitizen(
    string Id,
    string Uuid,
    string Name,
    string DateOfBirth,
    string Cpr,
    string? UserName,
    string Amr,
    string Loa,
    string Pid,
    string? Rule);

/// <summary>
/// Which gender the generated personal number encodes, in its last digit, as a real one does.
/// </summary>
/// <remarks>
/// StubId.Wire has a gender of its own, which this is not: the client package references nothing
/// at all, so it repeats the two values rather than pulling another assembly in behind them.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<StubIdGender>))]
public enum StubIdGender
{
    /// <summary>An even last digit on the generated personal number.</summary>
    Female,

    /// <summary>An odd last digit on the generated personal number.</summary>
    Male,
}

/// <summary>What to create a citizen from.</summary>
/// <remarks>
/// There is no personal number here and there will not be one. StubID generates a replacement
/// number whose day of month is raised into the 61-91 range, which no issued CPR number uses, so
/// a number it produces cannot belong to anybody. Accepting one would be accepting real personal
/// data into a test fixture. Read the generated number from <see cref="StubIdCitizen.Cpr" />.
/// </remarks>
public sealed record CitizenSpec
{
    /// <summary>
    /// The full name, which reaches a client as the <c>mitid.identity_name</c> claim.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// A date, not a string: the wire wants yyyy-MM-dd and a malformed one is a 500 rather than a
    /// refusal, so the type keeps the mistake from being possible.
    /// </summary>
    public required DateOnly DateOfBirth { get; init; }

    /// <summary>Chosen, so a test can name its people. Generated when omitted.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Sets the last digit of the generated personal number, odd for male and even for female.
    /// Omitted counts as female.
    /// </summary>
    public StubIdGender? Gender { get; init; }

    /// <summary>
    /// What the simulation parameter's <c>username</c> directive resolves against. Omitted leaves
    /// the person reachable by uuid or personal number.
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Null approves. Anything else is the broker error code a login as this person fails with,
    /// however that person was chosen - including an explicit approval naming them.
    /// </summary>
    public string? Rule { get; init; }
}
