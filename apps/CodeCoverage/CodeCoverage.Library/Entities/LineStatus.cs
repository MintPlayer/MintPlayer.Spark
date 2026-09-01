namespace CodeCoverage.Entities;

/// <summary>
/// Coverage state of a coverable line. Non-coverable lines are simply absent
/// from the data. Ordered so that merging sessions can take the max: more
/// executed wins.
/// </summary>
public enum LineStatus
{
    NotCovered = 0,
    PartiallyCovered = 1,
    Covered = 2,
}
