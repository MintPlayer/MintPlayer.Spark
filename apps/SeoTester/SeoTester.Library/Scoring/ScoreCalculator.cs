using SeoTester.Catalogue;

namespace SeoTester.Scoring;

/// <summary>How one check came out on one page, or once for a site-level check (PRD §7).</summary>
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,

    /// <summary>The check does not apply (no hreflang on the page, no PWA profile): left out of the score.</summary>
    NotApplicable,

    /// <summary>The check could not be measured (timeout, refused fetch): left out of the score, never a fail.</summary>
    Error,
}

/// <summary>
/// What a run measured for one check: how many pages (or, for a site-level check, the one measurement) passed,
/// warned, failed, did not apply or could not be measured.
/// </summary>
public sealed record CheckOutcome(string CheckId, int Pass, int Warn, int Fail, int NotApplicable = 0, int Error = 0)
{
    /// <summary>A site-level check: one measurement with <paramref name="status"/>.</summary>
    public static CheckOutcome Site(string checkId, CheckStatus status) => status switch
    {
        CheckStatus.Pass => new(checkId, 1, 0, 0),
        CheckStatus.Warn => new(checkId, 0, 1, 0),
        CheckStatus.Fail => new(checkId, 0, 0, 1),
        CheckStatus.NotApplicable => new(checkId, 0, 0, 0, NotApplicable: 1),
        _ => new(checkId, 0, 0, 0, Error: 1),
    };

    /// <summary>Pages that count: N/A and errors are excluded from the denominator (§7).</summary>
    public int Measured => Pass + Warn + Fail;
}

/// <summary>A check's result as scored.</summary>
/// <param name="Ratio">0–1: (pass + ½ warn) / measured; null when nothing was measured.</param>
public sealed record CheckScore(string CheckId, CheckCategory Category, CheckSeverity Severity, int Weight, CheckStatus Status, double? Ratio, int Affected);

/// <summary>A category's result.</summary>
/// <param name="Score">0–100, null when no scored check in it was measured ("not measured").</param>
/// <param name="Capped">A critical check failed and held the score at <see cref="ScoreCalculator.CriticalCap"/>.</param>
public sealed record CategoryScore(CheckCategory Category, int? Score, string? Grade, bool Capped, int Weight);

/// <summary>A run's scores.</summary>
/// <param name="Overall">0–100, null when no weighted category was measured.</param>
public sealed record ScoreCard(int? Overall, string? Grade, IReadOnlyList<CategoryScore> Categories, IReadOnlyList<CheckScore> Checks, string ScoringVersion);

/// <summary>
/// The scoring engine (PRD §7, D15, D32). Pure and deterministic: the same snapshot and outcomes give the same
/// card, whatever their order.
/// <list type="number">
/// <item>A check's ratio is (pass + ½ warn) / (pass + warn + fail); N/A and errors are left out. Page-level checks
/// aggregate this way across pages; a site-level check is one measurement. Its status is Fail when anything failed,
/// else Warn when anything warned, else Pass; with nothing measured, Error when an error was seen, else N/A.</item>
/// <item>A category's score is the weight-weighted mean of its measured checks' ratios, × 100. Checks of weight 0
/// (Info) are shown, never scored. A category without a measured, weighted check is "not measured" (null).</item>
/// <item>A critical check whose ratio is below <see cref="CriticalCapThreshold"/> caps its category at
/// <see cref="CriticalCap"/> (Lighthouse-style: a site without titles cannot score a B in SEO).</item>
/// <item>The headline score is the category-weight-weighted mean of the measured categories. Categories of weight 0
/// (Domain, an information panel) are scored and shown but do not count.</item>
/// <item>Scores are rounded half away from zero only at the end; the headline uses the unrounded category means.</item>
/// <item>Grades: A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F.</item>
/// </list>
/// Outcomes for a check the snapshot does not hold (disabled, unknown, no implementation) are ignored.
/// </summary>
public static class ScoreCalculator
{
    /// <summary>A capped category scores at most this: an F.</summary>
    public const int CriticalCap = 49;

    /// <summary>A critical check below this ratio (it failed on at least half its pages) caps its category.</summary>
    public const double CriticalCapThreshold = 0.5;

    public static ScoreCard Score(ScoringSnapshot snapshot, IEnumerable<CheckOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(outcomes);

        // Several outcomes for one check (e.g. one per crawl batch) add up.
        var byCheck = outcomes
            .GroupBy(o => o.CheckId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new CheckOutcome(g.Key, g.Sum(o => o.Pass), g.Sum(o => o.Warn), g.Sum(o => o.Fail), g.Sum(o => o.NotApplicable), g.Sum(o => o.Error)), StringComparer.Ordinal);

        var checks = new List<CheckScore>();
        foreach (var check in snapshot.Checks.OrderBy(c => c.CheckId, StringComparer.Ordinal))
        {
            if (!byCheck.TryGetValue(check.CheckId, out var outcome))
                continue;
            checks.Add(new CheckScore(check.CheckId, check.Category, check.Severity, check.Weight, StatusOf(outcome), RatioOf(outcome), outcome.Fail + outcome.Warn));
        }

        var categories = new List<CategoryScore>();
        double weightedSum = 0, weightTotal = 0;
        foreach (var category in Enum.GetValues<CheckCategory>())
        {
            var weight = snapshot.WeightOf(category);
            var scored = checks.Where(c => c.Category == category && c.Weight > 0 && c.Ratio is not null).ToList();
            if (scored.Count == 0)
            {
                if (snapshot.Checks.Any(c => c.Category == category) || weight > 0)
                    categories.Add(new CategoryScore(category, null, null, false, weight));
                continue;
            }

            var mean = scored.Sum(c => c.Weight * c.Ratio!.Value) / scored.Sum(c => c.Weight) * 100;
            var capped = scored.Any(c => c.Severity == CheckSeverity.Critical && c.Ratio < CriticalCapThreshold) && mean > CriticalCap;
            if (capped)
                mean = CriticalCap;

            var rounded = Round(mean);
            categories.Add(new CategoryScore(category, rounded, GradeOf(rounded), capped, weight));
            if (weight > 0)
            {
                weightedSum += weight * mean;
                weightTotal += weight;
            }
        }

        int? overall = weightTotal > 0 ? Round(weightedSum / weightTotal) : null;
        return new ScoreCard(overall, overall is { } o ? GradeOf(o) : null, categories, checks, snapshot.Version);
    }

    /// <summary>A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F (D15).</summary>
    public static string GradeOf(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 50 => "D",
        _ => "F",
    };

    private static double? RatioOf(CheckOutcome outcome)
        => outcome.Measured == 0 ? null : (outcome.Pass + 0.5 * outcome.Warn) / outcome.Measured;

    private static CheckStatus StatusOf(CheckOutcome outcome)
    {
        if (outcome.Fail > 0) return CheckStatus.Fail;
        if (outcome.Warn > 0) return CheckStatus.Warn;
        if (outcome.Pass > 0) return CheckStatus.Pass;
        return outcome.Error > 0 ? CheckStatus.Error : CheckStatus.NotApplicable;
    }

    private static int Round(double value) => (int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
}
