using StubId.Abstractions;

namespace StubId.Wire;

public enum Gender
{
    Female,
    Male,
}

/// <summary>
/// Generates Danish personal numbers that cannot belong to anyone.
/// </summary>
/// <remarks>
/// <para>
/// Every generated number uses the official replacement scheme: the day of the month is raised
/// into the 61-91 range, which no issued number occupies. That is not a convention this project
/// invented — it is what the CPR office specifies for replacement numbers, and the broker's own
/// simulation parameter documents accepting one.
/// </para>
/// <para>
/// It matters because roughly ten million real numbers are in use. A plausible date with four
/// digits after it has a high chance of belonging to a living person, so a generator that
/// produced realistic-looking numbers would be handing out other people's identifiers.
/// </para>
/// <para>
/// Modulus 11 is deliberately not applied. It stopped being a validity test in 2007, when the
/// office began issuing numbers that fail it, so computing it would suggest a check that means
/// nothing either way.
/// </para>
/// </remarks>
[Fidelity(FidelityTier.Shape, FidelityProvenance.DocsConfirmed,
    Evidence = "https://www.cpr.dk/media/12068/erstatningspersonnummerets-opbygning.pdf")]
public static class Cpr
{
    /// <summary>The official day mapping: 01-09 to 61-69, 10-19 to 70-79, and so on.</summary>
    private static int ReplacementDay(int day) => day switch
    {
        >= 1 and <= 9 => day + 60,
        >= 10 and <= 19 => day + 60,
        >= 20 and <= 29 => day + 60,
        >= 30 and <= 31 => day + 60,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Not a day of a month."),
    };

    /// <summary>
    /// A replacement number for a date of birth, with the last digit encoding the gender as a
    /// real one does: odd for male, even for female.
    /// </summary>
    /// <param name="sequence">
    /// Distinguishes people born on the same day. Deterministic on purpose: a seeded fixture or
    /// a test that generates the same citizen twice should get the same number.
    /// </param>
    public static string Generate(DateOnly dateOfBirth, Gender gender, int sequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        var serial = 1 + (sequence * 2);
        if (serial > 9999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence), sequence, "More people than one day's numbers can hold.");
        }

        // The last digit carries the gender, so the sequence steps in twos and the parity is
        // set at the end.
        var lastDigit = gender == Gender.Male ? serial | 1 : serial & ~1;
        var value = (serial / 10 * 10) + (lastDigit % 10);

        return $"{ReplacementDay(dateOfBirth.Day):00}{dateOfBirth.Month:00}"
             + $"{dateOfBirth.Year % 100:00}{value:0000}";
    }

    /// <summary>
    /// Whether a number is one of ours: a day in the 61-91 range, which no issued number has.
    /// </summary>
    public static bool IsReplacementNumber(string cpr)
    {
        var digits = cpr.Replace("-", "", StringComparison.Ordinal);

        return digits.Length == 10
            && int.TryParse(digits.AsSpan(0, 2), out var day)
            && day is >= 61 and <= 91;
    }
}
