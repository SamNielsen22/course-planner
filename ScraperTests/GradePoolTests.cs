using CoursePlanner.Data;

namespace ScraperTests;

/// <summary>Pooling published grade rows into one distribution.</summary>
public class GradePoolTests
{
    private static GradeIndex.Row Row(double avg, int a = 0, int b = 0, int c = 0) =>
        new("Spring2024", "ECON", "4650", null, avg, null, null, null, null, a, b, c, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void TheAverageIsWeightedByHeadcount()
    {
        var pooled = GradeIndex.Pool([Row(4.0, a: 30), Row(2.0, c: 10)]);
        Assert.Equal(3.5, pooled.AvgGpa!.Value, 2);
        Assert.Equal(30, pooled.A);
        Assert.Equal(10, pooled.C);
    }

    [Fact]
    public void ASectionWithEveryCountSuppressedStillCounts()
    {
        // Spring 2024 ECON 4650 as published: one visible section and two whose
        // counts were all blanked. Weighed as nothing, the pool was 3.95 against
        // the registrar's 3.12; weighed as small sections it lands near it.
        var pooled = GradeIndex.Pool([Row(3.95, a: 24), Row(2.06), Row(2.64)]);
        var expected = (3.95 * 24 + 2.06 * GradeIndex.SuppressedWeight + 2.64 * GradeIndex.SuppressedWeight) / (24 + 2 * GradeIndex.SuppressedWeight);
        Assert.Equal(expected, pooled.AvgGpa!.Value, 6);
        Assert.True(pooled.AvgGpa < 3.6);
        // The bars stay exactly as published: nothing is invented for the blanked sections.
        Assert.Equal(24, pooled.A);
        Assert.Equal(0, pooled.B);
    }

    [Fact]
    public void OneRowIsReadAsPublished()
    {
        var pooled = GradeIndex.Pool([Row(2.06)]);
        Assert.Equal(2.06, pooled.AvgGpa);
        Assert.Equal(0, pooled.A);
    }
}
