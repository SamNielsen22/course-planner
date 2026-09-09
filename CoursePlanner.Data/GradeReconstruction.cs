namespace CoursePlanner.Data;

/// <summary>
/// Rebuilds a published grade row's list of grades, so that rows can be pooled.
///
/// Percentiles cannot be averaged: a percentile is the grade of the student
/// at a given position in the sorted list of everyone, and the position in a
/// merged list depends on every grade, not on the parts' percentiles. But a
/// published row - a section, or a term of a course - is a set of constraints
/// on one hidden list over the twelve marks (E, D-, D, D+ ... A-, A):
///
///  - the five bucket counts fix the running total at five marks;
///  - the three percentiles, which the dashboard computes by linear
///    interpolation, pin or bound three more. An off-grid value such as 3.85
///    says exactly how many students sit at or below the lower mark;
///  - the mean fixes the total grade-points, which settles the +/- splits
///    inside the buckets;
///  - the standard deviation is computed over the suppressed students too.
///    A hidden E is a zero, enormous in a deviation, so trying each hidden
///    count (0-4 per blanked bucket) and keeping the one that reproduces the
///    published deviation recovers what suppression hid.
///
/// Measured against the registrar's own pooled figure for 5,689 term-courses
/// (each course's sections in one term, versus the whole-course row for that
/// term): 25th percentile exact 80%, 50th 92%, 75th 96%; the misses are a
/// single +/- step. The pooled mean, weighted by the headcounts inferred
/// here, is within 0.01 of the registrar's 91% of the time.
///
/// Runs once per row when <see cref="GradeIndex"/> is built, never per request.
/// </summary>
public static class GradeReconstruction
{
    /// <summary>One published row. Bucket counts are null where the registrar blanked them (under five).</summary>
    public readonly record struct Row(
        double Avg, double? P25, double? P50, double? P75, double? Sd,
        int? A, int? B, int? C, int? D, int? E);

    private static readonly double[] Grid = [0, .7, 1.0, 1.3, 1.7, 2.0, 2.3, 2.7, 3.0, 3.3, 3.7, 4.0];

    // Each bucket's marks, low to high, in E D C B A order. The last mark is the
    // bucket's top, where the bucket count fixes the running total.
    private static readonly int[][] Buckets = [[0], [1, 2, 3], [4, 5, 6], [7, 8, 9], [10, 11]];
    private static readonly HashSet<int> Fixed = [0, 3, 6, 9, 11];

    private const int MaxHidden = 4;   // a blanked bucket holds 0-4 students

    private static double Rank(double q, int n) => 1 + q * (n - 1);

    /// <summary>
    /// The row's most plausible list of grades: running totals at each of the
    /// twelve marks, and the headcount including the inferred hidden students.
    /// Null when the row has no students at all.
    /// </summary>
    public static (double[] List, int Headcount)? Rebuild(Row row)
    {
        int?[] raw = [row.E, row.D, row.C, row.B, row.A];
        var hidden = Enumerable.Range(0, 5).Where(k => raw[k] is null).ToArray();
        var combos = (int)Math.Pow(MaxHidden + 1, hidden.Length);
        (double Score, Fit Fit)? best = null;
        for (var m = 0; m < combos; m++)
        {
            var counts = new int[5];
            for (var k = 0; k < 5; k++) counts[k] = raw[k] ?? 0;
            var sum = 0;
            for (var h = 0; h < hidden.Length; h++)
            {
                // first blanked bucket varies slowest
                var digit = m / (int)Math.Pow(MaxHidden + 1, hidden.Length - 1 - h) % (MaxHidden + 1);
                counts[hidden[h]] = digit; sum += digit;
            }
            var fit = Rebuild(row, counts);
            if (fit is null) continue;
            var score = (row.Sd is { } sd ? Math.Abs(SampleSd(fit.C, fit.N) - sd) : 0) + fit.Violation + 0.001 * sum;
            if (best is null || score < best.Value.Score) best = (score, fit);   // strict: ties keep the first
        }
        return best is null ? null : (best.Value.Fit.C, best.Value.Fit.N);
    }

    /// <summary>The percentile of a list, read the way the dashboard reads it - linear interpolation.</summary>
    public static double Percentile(double[] list, int n, double q)
    {
        var r = Rank(q, n); var fl = Math.Floor(r); var ce = Math.Ceiling(r);
        var xf = Grid[First(list, fl)]; var xc = Grid[First(list, ce)];
        return xf + (r - fl) * (xc - xf);
    }

    /// <summary>One rebuilt list: running totals at each mark, headcount, and how far the constraints had to bend.</summary>
    private sealed record Fit(double[] C, int N, double Violation);

    /// <summary>Rebuild the list for one assignment of hidden counts (E D C B A order).</summary>
    private static Fit? Rebuild(Row row, int[] counts)
    {
        var n = counts.Sum();
        if (n <= 0) return null;

        var c = new double[12]; var lb = new double[12]; var ub = new double[12];
        Array.Fill(ub, n);
        var run = 0;
        for (var k = 0; k < 5; k++)
        {
            run += counts[k];
            var top = Buckets[k][^1];
            c[top] = run; lb[top] = ub[top] = run;
        }

        // Quantile anchors. A constraint that lands on a fixed mark and
        // disagrees with it is a contradiction: the hidden counts are wrong.
        var violation = 0.0;
        foreach (var (q, v) in new[] { (.25, row.P25), (.5, row.P50), (.75, row.P75) })
        {
            if (v is null) continue;
            var r = Rank(q, n); var fl = Math.Floor(r); var ce = Math.Ceiling(r);
            var i = GridIndex(v.Value);
            if (i >= 0)
            {
                if (Fixed.Contains(i)) violation += Math.Max(0, ce - c[i]);
                else lb[i] = Math.Max(lb[i], ce);
                if (i > 0)
                {
                    if (Fixed.Contains(i - 1)) violation += Math.Max(0, c[i - 1] - (fl - 1));
                    else ub[i - 1] = Math.Min(ub[i - 1], fl - 1);
                }
            }
            else
            {
                var lo = LowerIndex(v.Value);
                if (Fixed.Contains(lo)) violation += Math.Abs(c[lo] - fl);
                else { lb[lo] = Math.Max(lb[lo], fl); ub[lo] = Math.Min(ub[lo], fl); }
                if (lo > 0)
                {
                    if (Fixed.Contains(lo - 1)) violation += Math.Max(0, c[lo - 1] - (fl - 1));
                    else ub[lo - 1] = Math.Min(ub[lo - 1], fl - 1);
                }
            }
        }
        for (var i = 0; i < 12; i++)
        {
            if (lb[i] > ub[i]) { violation += lb[i] - ub[i]; (lb[i], ub[i]) = (ub[i], lb[i]); }
        }

        // Even split inside each bucket, then into bounds, then monotone.
        foreach (var idx in Buckets)
        {
            if (idx.Length == 1) continue;
            var floor = idx[0] > 0 ? c[idx[0] - 1] : 0;
            var size = c[idx[^1]] - floor;
            for (var k = 0; k < idx.Length - 1; k++) c[idx[k]] = floor + size * (k + 1.0) / idx.Length;
        }
        for (var i = 0; i < 12; i++) c[i] = Math.Min(Math.Max(c[i], lb[i]), ub[i]);
        for (var i = 1; i < 12; i++) c[i] = Math.Max(c[i], c[i - 1]);
        for (var i = 10; i >= 0; i--) c[i] = Math.Min(c[i], c[i + 1]);

        // Move students within their buckets until the mean matches.
        var delta = row.Avg * n - Total(c);
        for (var pass = 0; pass < 3; pass++)
        {
            var room = new double[11]; var cap = 0.0;
            for (var i = 0; i < 11; i++)
            {
                if (Fixed.Contains(i)) continue;
                var s = delta > 0
                    ? c[i] - Math.Max(lb[i], i > 0 ? c[i - 1] : 0)
                    : Math.Min(ub[i], c[i + 1]) - c[i];
                room[i] = Math.Max(0, s);
                cap += (Grid[i + 1] - Grid[i]) * room[i];
            }
            if (cap <= 1e-9) break;
            var t = Math.Min(1.0, Math.Abs(delta) / cap);
            for (var i = 0; i < 11; i++) c[i] += (delta > 0 ? -1 : 1) * t * room[i];
            delta = row.Avg * n - Total(c);
            if (Math.Abs(delta) < 0.01 * n) break;
        }
        violation += Math.Abs(delta) / n * 10;   // an unmatched mean, in units comparable to a deviation miss

        return new Fit(c, n, violation);
    }

    private static int First(double[] c, double k)
    {
        for (var i = 0; i < 12; i++) if (c[i] >= k - 1e-9) return i;
        return 11;
    }

    private static double Total(double[] c)
    {
        var t = 0.0;
        for (var i = 0; i < 12; i++) t += Grid[i] * (c[i] - (i > 0 ? c[i - 1] : 0));
        return t;
    }

    // Sample deviation (n-1): the convention that fits the fully visible sections best.
    private static double SampleSd(double[] c, int n)
    {
        var mean = Total(c) / n; var ss = 0.0;
        for (var i = 0; i < 12; i++) ss += (c[i] - (i > 0 ? c[i - 1] : 0)) * (Grid[i] - mean) * (Grid[i] - mean);
        return Math.Sqrt(ss / (n > 1 ? n - 1 : n));
    }

    private static int GridIndex(double v)
    {
        for (var i = 0; i < 12; i++) if (Math.Abs(Grid[i] - v) < 0.005) return i;
        return -1;
    }

    private static int LowerIndex(double v)
    {
        var lo = 0;
        for (var i = 0; i < 12; i++) if (Grid[i] < v) lo = i;
        return lo;
    }
}
