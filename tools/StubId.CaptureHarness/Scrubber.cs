using System.Text.RegularExpressions;

namespace StubId.CaptureHarness;

/// <summary>
/// Replaces values that must not sit in the repository with stable placeholders, and puts
/// them back when a fixture is replayed.
/// </summary>
/// <remarks>
/// Substitution is a byte-level splice on purpose. Parsing a document and re-serialising it
/// would normalise whitespace and member order, which are exactly the facts the fixtures
/// exist to pin.
/// </remarks>
public static partial class Scrubber
{
    /// <summary>
    /// The broker publishes these credentials openly so anyone can exercise its test
    /// environment. They are still replaced in fixtures: a recorded HTTP exchange
    /// containing something shaped like a secret trips every scanner that looks at this
    /// repository, and the argument "but that one is fine" does not survive review.
    /// </summary>
    private static readonly (string Placeholder, string Value)[] Secrets =
    [
        ("{{NEB_PP_OPEN_CLIENT_CODE_SECRET}}",
            "rnlguc7CM/wmGSti4KCgCkWBQnfslYr0lMDZeIFsCJweROTROy2ajEigEaPQFl76Py6AVWnhYofl/0oiSAgdtg=="),
    ];

    public static string Scrub(string text)
    {
        foreach (var (placeholder, value) in Secrets)
        {
            text = text.Replace(value, placeholder, StringComparison.Ordinal);
        }

        return text;
    }

    public static string Unscrub(string text)
    {
        foreach (var (placeholder, value) in Secrets)
        {
            text = text.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Finds anything shaped like a Danish CPR number. Used by the fixture guard rather than
    /// for scrubbing: nothing should contain one at all, and if something does the build
    /// should stop rather than quietly mask it.
    /// </summary>
    public static Match FindCprShapedText(string candidate) => CprPattern().Match(candidate);

    /// <summary>
    /// Ten digits opening with a plausible day and month, standing alone rather than sitting
    /// inside a longer alphanumeric run.
    ///
    /// The boundary is not fussiness. Certificate thumbprints are long hex strings and one
    /// of the broker's contains "3007045896", which reads as the 30th of July. A real CPR in
    /// a recording appears as its own value - "cpr":"..." or cpr=... - so requiring it to
    /// stand alone keeps the check useful instead of permanently red.
    ///
    /// It still over-reports on purpose. A false positive costs a minute; a miss is a
    /// disclosure. Replacement numbers use a day of 61-91 and never match.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z])(0[1-9]|[12]\d|3[01])(0[1-9]|1[0-2])\d{6}(?![0-9A-Za-z])")]
    private static partial Regex CprPattern();
}
