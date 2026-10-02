using MintPlayer.Spark.Abstractions.Authorization;

[assembly: SparkReservedActions(typeof(MintPlayer.Spark.Moderation.ModerationRights))]

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// The rights Moderation asks <c>security.json</c> about. Content rights are granted per type
/// (<c>Vote/Question</c>); the rest name the <see cref="Target"/> pseudo-type
/// (<c>Review/Moderation</c>). No wildcards (D3): grant each by name.
/// </summary>
public static class ModerationRights
{
    /// <summary>The target of the rights that are not about one entity type.</summary>
    [SparkNotAnAction]
    public const string Target = "Moderation";

    /// <summary><c>Vote/T</c>: cast an up-vote on a <c>T</c>.</summary>
    public const string Vote = "Vote";

    /// <summary><c>Downvote/T</c>: cast a down-vote on a <c>T</c>.</summary>
    public const string Downvote = "Downvote";

    /// <summary><c>Flag/T</c>: flag a <c>T</c> for review.</summary>
    public const string Flag = "Flag";

    /// <summary><c>Lock/T</c>: lock and unlock a <c>T</c>; holders are exempt from locks on <c>T</c>.</summary>
    public const string Lock = "Lock";

    /// <summary><c>Review/Moderation</c>: read the review queue and decide cases.</summary>
    public const string Review = "Review";

    /// <summary><c>Suspend/Moderation</c>: suspend and unsuspend accounts, and merge sock puppets.</summary>
    public const string Suspend = "Suspend";

    /// <summary><c>Audit/Moderation</c>: read the moderation audit log.</summary>
    public const string Audit = "Audit";

    /// <summary>
    /// Actions no reputation-earned group may ever hold (D12): each is destructive or reaches rows a
    /// normal member cannot see. Also the list the <c>earnable</c> escape hatch is checked against.
    /// </summary>
    /// <remarks>
    /// <c>Purge</c>, <c>Restore</c> and <c>ViewDeleted</c> are SoftDelete's verbs and <c>Revert</c> is
    /// History's; this package references neither, so they are named here as literals. A test holds
    /// them to those packages' reserved-verb declarations (<c>SparkReservedActions</c>).
    /// </remarks>
    public static readonly IReadOnlySet<string> NeverEarnable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Lock, Suspend, "Purge", "Restore", "Revert", "ViewDeleted", Audit,
    };

    /// <summary>
    /// Actions a reputation-earned group may hold without the escape hatch: reading, contributing,
    /// editing (reversible through History), voting, flagging and reviewing.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultEarnable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SparkCoreActions.Query, SparkCoreActions.Read, SparkCoreActions.New, SparkCoreActions.Edit, Vote, Downvote, Flag, Review,
    };
}
