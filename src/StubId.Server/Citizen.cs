namespace StubId.Server;

/// <summary>
/// A person the stub can authenticate as.
/// </summary>
/// <param name="Uuid">The MitID identifier, stable for this person across every service.</param>
/// <param name="Name">Shown to the service, and in the stub's own login page.</param>
/// <param name="DateOfBirth">ISO date. The broker returns it as a string.</param>
/// <param name="Cpr">
/// A replacement number: the day of the month is raised into the 61-91 range, which by
/// construction cannot belong to a living person.
/// </param>
/// <param name="Amr">How they authenticated, in the broker's vocabulary.</param>
/// <param name="Loa">One of Low, Substantial or High.</param>
public sealed record Citizen(
    string Uuid,
    string Name,
    string DateOfBirth,
    string Cpr,
    string Amr = "code_app",
    string Loa = "Substantial")
{
    public int Age(DateTimeOffset now) =>
        (int)((now - DateTimeOffset.Parse(DateOfBirth, System.Globalization.CultureInfo.InvariantCulture)).TotalDays / 365.2425);
}
