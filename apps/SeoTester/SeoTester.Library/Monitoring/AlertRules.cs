using SeoTester.Catalogue;
using SeoTester.Scoring;

namespace SeoTester.Monitoring;

/// <summary>Why a monitored site's members are alerted (<c>docs/seo_analyzer_PRD.md</c> F10).</summary>
public enum SiteAlertKind
{
    /// <summary>The headline score fell by at least the site's threshold since the previous completed run.</summary>
    ScoreDrop,

    /// <summary>A check of severity Critical fails that did not fail (or warn) on the previous completed run.</summary>
    NewCriticalIssue,

    /// <summary>The certificate expires within 14 days (<c>security.certificate-expiry</c> fails).</summary>
    CertificateExpiring,

    /// <summary>The domain registration expires within 30 days (<c>domain.expiry</c> fails).</summary>
    DomainExpiring,
}

/// <summary>One alert: its kind, the scores for a drop, the checks for an issue.</summary>
public sealed record SiteAlert(SiteAlertKind Kind, int? PreviousScore = null, int? Score = null, IReadOnlyList<string>? CheckIds = null);

/// <summary>A check of the finished run as the alert rules see it: its status, its New/Fixed/Unchanged delta, its severity in the run's snapshot.</summary>
public sealed record AlertCheck(string CheckId, CheckStatus Status, string? Delta, CheckSeverity Severity);

/// <summary>
/// The alert rules of F10, pure: what a completed run of a monitored site alerts about. Each expiry alert repeats on
/// every run while it fails (it gets more urgent, not less); a critical issue alerts once, when it is new; a score drop
/// compares only runs scored with the same weights (<c>ScoringVersion</c>), because an administrator's catalogue edit is
/// not the site getting worse.
/// </summary>
public static class AlertRules
{
    public const string CertificateExpiryCheck = "security.certificate-expiry";
    public const string DomainExpiryCheck = "domain.expiry";

    /// <param name="previousScore">The previous completed run's score; null when there is none or it was not scored.</param>
    /// <param name="score">This run's score.</param>
    /// <param name="scoreDrop">The site's threshold in points; 0 or less switches score-drop alerts off.</param>
    /// <param name="sameScoring">Both runs were scored with the same snapshot (<c>ScoringVersion</c>).</param>
    /// <param name="checks">This run's checks.</param>
    public static IReadOnlyList<SiteAlert> Evaluate(int? previousScore, int? score, int scoreDrop, bool sameScoring, IEnumerable<AlertCheck> checks)
    {
        var alerts = new List<SiteAlert>();
        if (scoreDrop > 0 && sameScoring && previousScore is { } before && score is { } now && before - now >= scoreDrop)
            alerts.Add(new SiteAlert(SiteAlertKind.ScoreDrop, before, now));

        var list = checks.ToList();
        var critical = list
            .Where(c => c.Severity == CheckSeverity.Critical && c.Status == CheckStatus.Fail && c.Delta == "New")
            .Select(c => c.CheckId)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (critical.Count > 0)
            alerts.Add(new SiteAlert(SiteAlertKind.NewCriticalIssue, CheckIds: critical));

        if (list.Any(c => c.CheckId == CertificateExpiryCheck && c.Status == CheckStatus.Fail))
            alerts.Add(new SiteAlert(SiteAlertKind.CertificateExpiring, CheckIds: [CertificateExpiryCheck]));
        if (list.Any(c => c.CheckId == DomainExpiryCheck && c.Status == CheckStatus.Fail))
            alerts.Add(new SiteAlert(SiteAlertKind.DomainExpiring, CheckIds: [DomainExpiryCheck]));
        return alerts;
    }
}
