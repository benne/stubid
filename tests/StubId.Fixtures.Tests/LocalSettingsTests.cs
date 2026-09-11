using StubId.CaptureHarness;

namespace StubId.Fixtures.Tests;

/// <summary>
/// The two ways a setting is not filled in, both of which used to read as a value.
/// </summary>
/// <remarks>
/// Asserted against the pure half rather than through the file, because the file is whatever the
/// machine has and the environment cannot stand in for it: setting a variable to the empty string
/// deletes it, so a test written that way exercises the absent path and proves nothing about the
/// empty one.
/// </remarks>
public class LocalSettingsTests
{
    /// <summary>
    /// A blank line is somebody who has not filled it in, not a credential of length zero.
    /// </summary>
    /// <remarks>
    /// This read as configured everywhere downstream. The preflight printed "set, 0 characters"
    /// and then warned the value was under six, and an empty key identifier silenced the warning
    /// that a request object would name no key - so the object went out without one and earned a
    /// refusal the broker's error page declines to explain.
    /// </remarks>
    [Fact]
    public void A_blank_value_is_not_set()
    {
        Assert.Null(LocalSettings.Resolve("", "the subdomain your sandbox answers on"));
        Assert.Null(LocalSettings.Resolve("", null));
    }

    /// <summary>Someone copied the example file and did not fill this one in.</summary>
    [Fact]
    public void The_examples_own_description_is_not_set()
    {
        Assert.Null(LocalSettings.Resolve("the primary client's secret", "the primary client's secret"));
    }

    [Fact]
    public void A_real_value_resolves_even_where_the_example_names_nothing()
    {
        Assert.Equal("an-account", LocalSettings.Resolve("an-account", null));
        Assert.Equal("an-account", LocalSettings.Resolve("an-account", "a description"));
    }

    /// <summary>
    /// A value that happens to read like prose is still a value, as long as it is not that name's
    /// own description.
    /// </summary>
    [Fact]
    public void Another_names_description_does_not_blank_this_one()
    {
        Assert.Equal(
            "the primary client's secret",
            LocalSettings.Resolve("the primary client's secret", "the claims client's secret"));
    }
}
