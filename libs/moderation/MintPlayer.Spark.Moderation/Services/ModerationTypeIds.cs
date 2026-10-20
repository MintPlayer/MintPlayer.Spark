using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// How moderation stores a target's type (D28): its persistent-object id, <c>"D"</c>-formatted. A name
/// is what an application's <c>[assembly: PersistentObjectName]</c> changes, so a stored name would point
/// at nothing (or at another type that took the name) after a rename; the id never changes.
/// </summary>
internal static class ModerationTypeIds
{
    public static string Of(EntityTypeDefinition definition) => definition.Id.ToString("D");
}
