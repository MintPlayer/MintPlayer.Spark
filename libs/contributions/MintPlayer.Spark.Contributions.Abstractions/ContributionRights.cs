using MintPlayer.Spark.Abstractions.Authorization;

[assembly: SparkReservedActions(typeof(MintPlayer.Spark.Contributions.ContributionRights))]

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The <c>security.json</c> action Contributions adds, granted per generated contribution type by name
/// (<c>RevertContribution/SongLyricsContribution</c>). There is no <c>Contribute</c> verb: contributors
/// hold <c>Edit</c> on the target, narrowed by attribute-level rights (PRD Q11–Q13).
/// </summary>
/// <remarks>
/// Everything else reuses existing verbs: <c>Query</c>/<c>Read</c> (history), <c>Delete</c> (hide),
/// SoftDelete's <c>Restore</c>/<c>ViewDeleted</c>/<c>Purge</c> on the contribution type, and
/// <c>Delete</c> on the current type (remove a version).
/// </remarks>
public static class ContributionRights
{
    /// <summary>
    /// <c>RevertContribution/T</c>: make a contribution current again by hiding every newer non-hidden
    /// contribution in its slot, atomically and audited, never re-attributing.
    /// </summary>
    public const string RevertContribution = "RevertContribution";
}
