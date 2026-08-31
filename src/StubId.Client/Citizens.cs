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

[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<StubIdGender>))]
public enum StubIdGender
{
    Female,
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
    public required string Name { get; init; }

    /// <summary>
    /// A date, not a string: the wire wants yyyy-MM-dd and a malformed one is a 500 rather than a
    /// refusal, so the type keeps the mistake from being possible.
    /// </summary>
    public required DateOnly DateOfBirth { get; init; }

    /// <summary>Chosen, so a test can name its people. Generated when omitted.</summary>
    public string? Id { get; init; }

    public StubIdGender? Gender { get; init; }

    public string? UserName { get; init; }

    /// <summary>
    /// Null approves. Anything else is the broker error code a login as this person fails with,
    /// however that person was chosen - including an explicit approval naming them.
    /// </summary>
    public string? Rule { get; init; }
}
