namespace StubId.Wire.Tests;

public class CprTests
{
    [Theory]
    [InlineData(1, 61)]
    [InlineData(9, 69)]
    [InlineData(14, 74)]
    [InlineData(28, 88)]
    [InlineData(31, 91)]
    public void The_day_is_raised_into_the_range_no_issued_number_occupies(int day, int expected)
    {
        var cpr = Cpr.Generate(new DateOnly(1986, 8, day), Gender.Female);

        Assert.Equal(expected, int.Parse(cpr[..2]));
    }

    [Fact]
    public void Everything_generated_is_a_replacement_number()
    {
        // The point of the whole class. Roughly ten million real numbers are in use, so a
        // generator producing realistic ones would be handing out other people's identifiers.
        for (var day = 1; day <= 28; day++)
        {
            for (var sequence = 0; sequence < 20; sequence++)
            {
                var cpr = Cpr.Generate(new DateOnly(1990, 3, day), Gender.Male, sequence);

                Assert.True(Cpr.IsReplacementNumber(cpr), cpr);
            }
        }
    }

    [Theory]
    [InlineData(Gender.Male, 1)]
    [InlineData(Gender.Female, 0)]
    public void The_last_digit_carries_the_gender_as_a_real_number_does(Gender gender, int parity)
    {
        for (var sequence = 0; sequence < 10; sequence++)
        {
            var cpr = Cpr.Generate(new DateOnly(1975, 12, 3), gender, sequence);

            Assert.Equal(parity, (cpr[^1] - '0') % 2);
        }
    }

    [Fact]
    public void People_born_on_one_day_get_different_numbers()
    {
        var born = new DateOnly(2001, 1, 1);
        var issued = Enumerable.Range(0, 50).Select(i => Cpr.Generate(born, Gender.Female, i)).ToList();

        Assert.Equal(issued.Count, issued.Distinct().Count());
    }

    [Fact]
    public void The_same_request_always_gives_the_same_number()
    {
        // A seeded fixture, or a test that creates the same citizen twice, should not get a
        // different person the second time.
        var born = new DateOnly(1999, 6, 15);

        Assert.Equal(Cpr.Generate(born, Gender.Male, 7), Cpr.Generate(born, Gender.Male, 7));
    }

    [Theory]
    // Impossible dates: the 31st of February and the 31st of April. They carry the shape of a
    // real number without being one, which is what the assertion needs and all it needs.
    [InlineData("3102999995")]
    [InlineData("3104999995")]
    public void A_number_that_could_be_someones_is_not_one_of_ours(string cpr)
    {
        Assert.False(Cpr.IsReplacementNumber(cpr));
    }

    [Fact]
    public void The_separated_form_is_recognised_too()
    {
        var cpr = Cpr.Generate(new DateOnly(1986, 8, 14), Gender.Male);

        Assert.True(Cpr.IsReplacementNumber($"{cpr[..6]}-{cpr[6..]}"));
    }
}
