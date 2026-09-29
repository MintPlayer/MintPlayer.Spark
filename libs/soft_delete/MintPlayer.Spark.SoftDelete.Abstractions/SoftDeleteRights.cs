namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// The <c>security.json</c> actions soft deletion adds. Granted per type by name
/// (<c>Restore/Car</c>, <c>Purge/Car</c>, <c>ViewDeleted/Car</c>) — there are no wildcards (#460, D3).
/// </summary>
/// <remarks>
/// None of them is implied by another right: <c>Edit</c> does not grant <c>Restore</c>, and
/// <c>Delete</c> does not grant <c>Purge</c>. All three are destructive or disclosing and are never
/// earnable through Moderation (D12).
/// </remarks>
public static class SoftDeleteRights
{
    /// <summary>Bring a soft-deleted row back.</summary>
    public const string Restore = "Restore";

    /// <summary>Remove a soft-deleted row permanently, with every revision of it (GDPR).</summary>
    public const string Purge = "Purge";

    /// <summary>
    /// See deleted rows: a query's <c>deleted: include|only</c> is honoured only for holders, and a
    /// save may point a reference at a deleted row only for holders of it on the target type.
    /// </summary>
    public const string ViewDeleted = "ViewDeleted";
}
