namespace CodeCoverage.Entities;

/// <summary>
/// Aggregated coverage numbers. Percentages are derived by consumers from the
/// covered/coverable pairs — never stored, so they can't drift.
/// </summary>
public class CoverageSummary
{
    /// <summary>Number of coverable lines executed at least once; divided by the coverable count this gives the line coverage percentage.</summary>
    public int LinesCovered { get; set; }
    /// <summary>Number of lines that tests could have executed, the denominator of the line coverage percentage.</summary>
    public int LinesCoverable { get; set; }
    /// <summary>Number of branch edges (if/else, switch arms, ...) that were taken at least once.</summary>
    public int BranchesCovered { get; set; }
    /// <summary>Total number of branch edges the reports know about, the denominator of the branch coverage percentage.</summary>
    public int BranchesTotal { get; set; }
    /// <summary>Number of source files these totals were aggregated from.</summary>
    public int FilesCount { get; set; }
}
