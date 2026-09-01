namespace CodeCoverage.Entities;

/// <summary>
/// Aggregated coverage numbers. Percentages are derived by consumers from the
/// covered/coverable pairs — never stored, so they can't drift.
/// </summary>
public class CoverageSummary
{
    public int LinesCovered { get; set; }
    public int LinesCoverable { get; set; }
    public int BranchesCovered { get; set; }
    public int BranchesTotal { get; set; }
    public int FilesCount { get; set; }
}
