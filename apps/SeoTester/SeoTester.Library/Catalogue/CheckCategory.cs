namespace SeoTester.Catalogue;

/// <summary>
/// The eight categories of the check catalogue (<c>docs/seo_analyzer_research.md</c> §1, PRD §3.1, §7). Seven are
/// scored with a weight from the settings (<c>SeoTester:Weights:*</c>); <see cref="Domain"/> is an information panel
/// plus alerts and carries no weight in the headline score.
/// </summary>
public enum CheckCategory
{
    Seo,
    Technical,
    Performance,
    Security,
    Accessibility,
    Social,
    Mobile,
    Domain,
}

/// <summary>
/// How much a check counts inside its category (research §1: Critical 10, High 6, Medium 3, Low 1, Info 0).
/// A failing <see cref="Critical"/> check caps its category (PRD §7); <see cref="Info"/> is shown, never scored.
/// </summary>
public enum CheckSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

/// <summary>Defaults that belong with <see cref="CheckSeverity"/>.</summary>
public static class CheckSeverityDefaults
{
    /// <summary>The research's weight for a severity; the shipped catalogue uses it, an administrator may change a check's weight.</summary>
    public static int Weight(CheckSeverity severity) => severity switch
    {
        CheckSeverity.Critical => 10,
        CheckSeverity.High => 6,
        CheckSeverity.Medium => 3,
        CheckSeverity.Low => 1,
        _ => 0,
    };
}
