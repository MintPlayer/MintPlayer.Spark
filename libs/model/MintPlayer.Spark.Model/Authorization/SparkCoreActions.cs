using MintPlayer.Spark.Abstractions.Authorization;

[assembly: SparkReservedActions(typeof(SparkCoreActions))]
[assembly: SparkReservedActions(typeof(SparkCombinedActions))]

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The <c>security.json</c> verbs core Spark asks for (<c>{action}/{target}</c>). Reserved: no custom
/// action may be named like one (see <see cref="SparkReservedActionsAttribute"/>). The combined verbs
/// are <see cref="SparkCombinedActions"/>'s, reserved alongside.
/// </summary>
public static class SparkCoreActions
{
    /// <summary><c>Query/T</c>: run a query over <c>T</c>.</summary>
    public const string Query = "Query";

    /// <summary><c>Read/T</c>: open one <c>T</c>.</summary>
    public const string Read = "Read";

    /// <summary><c>New/T</c>: create a <c>T</c>.</summary>
    public const string New = "New";

    /// <summary><c>Edit/T</c>: save an existing <c>T</c>.</summary>
    public const string Edit = "Edit";

    /// <summary><c>Delete/T</c>: delete a <c>T</c>.</summary>
    public const string Delete = "Delete";

    /// <summary><c>Replicate/Collection</c>: deploy an ETL script over a RavenDB collection (the target is the collection, not a model type).</summary>
    public const string Replicate = "Replicate";
}
