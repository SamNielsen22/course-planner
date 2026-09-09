using CoursePlanner.Data;

namespace Web.Pages;

/// <summary>How the pages print a grade figure.</summary>
public static class Figures
{
    public static string Gpa(double? value) => value?.ToString("0.00") ?? "—";
}
