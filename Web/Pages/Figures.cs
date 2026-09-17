using CoursePlanner.Data;

namespace Web.Pages;

/// <summary>How a grade figure is printed. An em dash when there is none.</summary>
public static class Figures
{
    public static string Gpa(double? value) => value?.ToString("0.00") ?? "—";
}
