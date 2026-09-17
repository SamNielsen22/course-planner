using Web.Schedule;

namespace Web.Tests;

/// <summary>
/// How meeting times are read and shown. These are pure functions, so they are
/// tested directly rather than through a page.
/// </summary>
public class MeetingTests
{
    /// <summary>
    /// A break is shown in the registrar's own shape, so the cart reads the same
    /// whether a row is a class or an hour kept free. The 24-hour values from the
    /// time picker become 12-hour with AM/PM, where midnight and noon are the
    /// cases a modulo gets wrong.
    /// </summary>
    [Theory]
    [InlineData("00:00", "12:00AM")]   // midnight is 12 AM, not 0
    [InlineData("00:30", "12:30AM")]
    [InlineData("09:05", "09:05AM")]
    [InlineData("11:59", "11:59AM")]
    [InlineData("12:00", "12:00PM")]   // noon is 12 PM, not 0
    [InlineData("12:45", "12:45PM")]
    [InlineData("13:00", "01:00PM")]
    [InlineData("23:59", "11:59PM")]
    public void ABreakShowsItsHoursAsTwelveHourTimes(string from, string shown)
    {
        var label = Meetings.Label(new Break("b1", "Work", "MoWe", from, from));
        Assert.Equal($"MoWe/{shown}-{shown}", label);
    }

    [Fact]
    public void ABreakWithNoDaysChosenCoversTheWeekdays()
    {
        Assert.Equal("MoTuWeThFr/08:00AM-09:00AM",
            Meetings.Label(new Break("b1", "Gym", "", "08:00", "09:00")));
    }

    /// <summary>
    /// The registrar sometimes lists the same meeting twice (AEROS 1110 carries
    /// its Thursday block two times). A repeat is never meaningful, so it is
    /// dropped rather than drawn twice or counted as a clash with itself.
    /// </summary>
    [Fact]
    public void ARepeatedMeetingIsShownOnce()
    {
        Assert.Equal("Th/03:30PM-05:30PM; TuWe/06:00AM-07:00AM",
            Meetings.Display("Th/03:30PM-05:30PM; TuWe/06:00AM-07:00AM; Th/03:30PM-05:30PM"));
    }

    [Fact]
    public void ARepeatedMeetingIsParsedOnce()
    {
        var once = Meetings.Parse("Th/03:30PM-05:30PM");
        var twice = Meetings.Parse("Th/03:30PM-05:30PM; Th/03:30PM-05:30PM");
        Assert.Equal(once.Count, twice.Count);
    }

    [Fact]
    public void MeetingsThatOnlyLookAlikeAreBothKept()
    {
        // Same hours, different days: two real meetings, not a duplicate.
        Assert.Equal(2, Meetings.Parse("Mo/09:00AM-10:00AM; We/09:00AM-10:00AM").Count);
    }

    [Fact]
    public void ASectionWithNoTimesSaysSoRatherThanShowingNothing()
    {
        Assert.Equal("No meeting time", Meetings.Display(null));
        Assert.Equal("No meeting time", Meetings.Display("   "));
    }
}
