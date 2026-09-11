using System.Security.Cryptography;
using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// What the preflight concludes, separated from the broker it would otherwise need.
/// </summary>
/// <remarks>
/// These two were the change's point and neither was asserted: a line printing PROBLEM used to
/// increment the warning count, and the report said "Ready to record." whenever it found nothing
/// wrong - which answers whether the configuration is complete rather than whether anything can
/// be recorded. Both are a one-word edit from returning.
/// <para>
/// The collection is not decoration. These capture <see cref="Console.Out" />, which is process
/// global, so they cannot run beside a test that writes to it.
/// </para>
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class PreflightTests
{
    [Fact]
    public void A_problem_is_reported_as_a_problem_and_not_as_a_warning()
    {
        Assert.Equal("1 problem(s), 0 warning(s).", Preflight.Verdict(1, 0, 19, "Signicat"));
    }

    [Fact]
    public void Nothing_wrong_and_cases_to_record_is_ready()
    {
        Assert.Equal("Ready to record.", Preflight.Verdict(0, 0, 26, "Nets eID Broker"));
    }

    /// <summary>
    /// Configured and unrecordable are different answers, and this one used to give the wrong one.
    /// </summary>
    [Fact]
    public void Nothing_wrong_and_no_cases_is_not_ready()
    {
        var verdict = Preflight.Verdict(0, 0, 0, "Signicat");

        Assert.DoesNotContain("Ready to record.", verdict, StringComparison.Ordinal);
        Assert.Contains("No case names Signicat yet", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_alone_is_still_not_ready()
    {
        Assert.Equal("0 problem(s), 1 warning(s).", Preflight.Verdict(0, 1, 26, "Nets eID Broker"));
    }

    /// <summary>A broker that signs with its secret has no key to report on.</summary>
    [Fact]
    public void A_broker_with_no_key_setting_reports_neither()
    {
        var (report, tally) = Capture(
            () => Preflight.ReportKeyMaterial(BrokerTarget.NetsEidBroker, _ => null));

        Assert.Equal(new Preflight.Tally(0, 0), tally);
        Assert.Contains("no key to register", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key path naming nothing is a problem, and the word and the count agree.
    /// </summary>
    [Fact]
    public void A_key_path_naming_nothing_is_a_problem()
    {
        var (report, tally) = Capture(() => Preflight.ReportKeyMaterial(
            BrokerTarget.Signicat,
            name => name == "STUBID_SIGNICAT_PRIVATE_KEY_PATH"
                ? Path.Combine(Path.GetTempPath(), "no-such-stubid-key.pem")
                : null));

        Assert.Equal(1, tally.Problems);
        Assert.Equal(0, tally.Warnings);
        Assert.Contains("PROBLEM", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message "names a file that is not there" is a lie when the file is there and the path
    /// was copied out of a shell, so the one thing that explains it is said.
    /// </summary>
    [Fact]
    public void A_tilde_in_the_key_path_is_explained_rather_than_expanded()
    {
        var (report, _) = Capture(() => Preflight.ReportKeyMaterial(
            BrokerTarget.Signicat,
            name => name == "STUBID_SIGNICAT_PRIVATE_KEY_PATH" ? "~/stubid-signicat.pem" : null));

        Assert.Contains("A leading ~ is not expanded here", report, StringComparison.Ordinal);
    }

    /// <summary>An unset key path is something to do, not something wrong.</summary>
    [Fact]
    public void An_unset_key_path_is_a_warning()
    {
        var (_, tally) = Capture(
            () => Preflight.ReportKeyMaterial(BrokerTarget.Signicat, _ => null));

        Assert.Equal(new Preflight.Tally(0, 1), tally);
    }

    /// <summary>
    /// A readable key with an identifier is the clean case, and a key without one is not.
    /// </summary>
    /// <remarks>
    /// The identifier half matters more than it looks. A request object that names no key is
    /// refused by a page that declines to say why, so the warning is the only thing that says so
    /// before a sitting rather than during one.
    /// </remarks>
    [Theory]
    [InlineData("a-registered-key", 0)]
    [InlineData(null, 1)]
    public void A_readable_key_warns_only_when_it_names_no_identifier(string? keyId, int warnings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stubid-preflight-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, RSA.Create(2048).ExportPkcs8PrivateKeyPem());

        try
        {
            var (report, tally) = Capture(() => Preflight.ReportKeyMaterial(
                BrokerTarget.Signicat,
                name => name == "STUBID_SIGNICAT_PRIVATE_KEY_PATH" ? path : keyId));

            Assert.Equal(new Preflight.Tally(0, warnings), tally);
            Assert.Contains("RS256, 2048 bits", report, StringComparison.Ordinal);

            // The half a person copies into the broker's dashboard.
            Assert.Contains("BEGIN PUBLIC KEY", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The report is written to the console, so reading it means capturing that.</summary>
    private static (string Report, Preflight.Tally Tally) Capture(Func<Preflight.Tally> act)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var tally = act();
            return (writer.ToString(), tally);
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
