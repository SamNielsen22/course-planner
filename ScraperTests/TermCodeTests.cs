namespace ScraperTests;

/// <summary>The term code both ways. A wrong code makes the pairing refresh silently fetch nothing.</summary>
public class TermCodeTests
{
    [Theory]
    [InlineData(2026, TermCodes.Fall, "1268", "Fall2026")]
    [InlineData(2027, TermCodes.Spring, "1274", "Spring2027")]
    [InlineData(2026, TermCodes.Summer, "1266", "Summer2026")]
    [InlineData(2020, TermCodes.Fall, "1208", "Fall2020")]
    public void CodeAndDisplayAreInverses(int year, int season, string code, string display)
    {
        Assert.Equal(code, TermCodes.Code(year, season));
        Assert.Equal(display, TermCodes.Display(code));
    }

    [Theory]
    [InlineData("1265")]     // 5 is not a season
    [InlineData("126")]
    [InlineData("12680")]
    [InlineData("2268")]     // wrong century digit
    [InlineData("abcd")]
    public void Display_RefusesAnythingThatIsNotATermCode(string code)
    {
        Assert.Throws<ArgumentException>(() => TermCodes.Display(code));
    }

    [Fact]
    public void Code_RefusesAnUnknownSeason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TermCodes.Code(2026, 5));
    }
}
