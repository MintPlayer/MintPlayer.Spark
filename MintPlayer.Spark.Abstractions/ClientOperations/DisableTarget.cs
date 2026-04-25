using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Scoping target for a <see cref="DisableActionOperation"/>. Discriminated by
/// <c>kind</c>. See <c>docs/PRD-ClientOperations.md</c> for the target shape
/// table.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PersistentObjectDisableTarget), "persistentObject")]
[JsonDerivedType(typeof(QueryDisableTarget),            "query")]
[JsonDerivedType(typeof(CurrentResponseDisableTarget),  "currentResponse")]
[JsonDerivedType(typeof(SessionDisableTarget),          "session")]
public abstract class DisableTarget { }

/// <summary>Disable attaches to a specific PersistentObject (by type + id).</summary>
public sealed class PersistentObjectDisableTarget : DisableTarget
{
    public required Guid ObjectTypeId { get; init; }
    public required string Id { get; init; }
}

/// <summary>Disable attaches to a specific query's result view.</summary>
public sealed class QueryDisableTarget : DisableTarget
{
    public required string QueryId { get; init; }
}

/// <summary>
/// Disable attaches to whatever the current endpoint is returning.
/// Frontend resolves the concrete target from response context.
/// </summary>
public sealed class CurrentResponseDisableTarget : DisableTarget { }

/// <summary>
/// Disable lasts the user's entire session. Rare — prefer security.json when
/// the disable is permission-driven.
/// </summary>
public sealed class SessionDisableTarget : DisableTarget { }
