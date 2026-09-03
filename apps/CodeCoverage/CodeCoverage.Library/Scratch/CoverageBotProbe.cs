namespace CodeCoverage.Scratch;

/// <summary>
/// THROWAWAY — do not merge. Exists only to move the coverage numbers on a
/// dogfood pull request so the sticky comment has something to report and the
/// patch-coverage figure lands below 100%.
/// <para>
/// Roughly half of the branches below are deliberately left untested.
/// </para>
/// </summary>
public static class CoverageBotProbe
{
    /// <summary>Covered by tests: every branch.</summary>
    public static string Band(double percent) => percent switch
    {
        >= 90 => "excellent",
        >= 75 => "good",
        >= 50 => "fair",
        _ => "poor",
    };

    /// <summary>Covered by tests: the happy path only.</summary>
    public static double Ratio(int covered, int coverable)
    {
        if (coverable <= 0) return 0;
        if (covered < 0) throw new ArgumentOutOfRangeException(nameof(covered));
        if (covered > coverable) throw new ArgumentOutOfRangeException(nameof(covered));
        return covered * 100.0 / coverable;
    }

    /// <summary>Deliberately NOT covered, so patch coverage has misses to report.</summary>
    public static string Describe(int covered, int coverable)
    {
        if (coverable == 0) return "nothing measured";

        var ratio = Ratio(covered, coverable);
        var band = Band(ratio);

        if (ratio >= 99.5) return $"{band}: essentially complete";
        if (ratio >= 80) return $"{band}: {coverable - covered} lines short";
        if (ratio >= 40) return $"{band}: about {coverable - covered} lines to go";
        return $"{band}: {covered} of {coverable}";
    }

    /// <summary>Deliberately NOT covered.</summary>
    public static string Trend(double before, double after)
    {
        var delta = after - before;
        if (Math.Abs(delta) < 0.05) return "flat";
        return delta > 0 ? $"up {delta:0.#} points" : $"down {Math.Abs(delta):0.#} points";
    }
}
