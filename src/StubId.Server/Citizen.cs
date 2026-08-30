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
/// <param name="Pid">
/// The legacy NemID identifier, returned under the nemid.pid scope. Documented as deprecated,
/// still sent.
/// </param>
public sealed record Citizen(
    string Uuid,
    string Name,
    string DateOfBirth,
    string Cpr,
    string Amr = "code_app",
    string Loa = "Substantial",
    string Pid = "9208-2002-2-000000000001")
{
    /// <summary>Shown to the user when the broker asks for a CPR. Base64, as sent.</summary>
    public string CprConsentHeader => "SW5kdGFzdCBkaXQgQ1BSLW51bW1lcg==";

    /// <summary>The body of that same prompt.</summary>
    public string CprConsentText => "U3R1YklEIGVtdWxlcmVyIE1pdElEIGkgdGVzdA==";

    public int Age(DateTimeOffset now)
    {
        // Calendar arithmetic, not days divided by an average year, which lands a year low on
        // the birthday itself in three years out of four.
        var born = DateOnly.Parse(DateOfBirth, System.Globalization.CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var age = today.Year - born.Year;
        return today < born.AddYears(age) ? age - 1 : age;
    }
}
