using System.ComponentModel;

namespace MintPlayer.Spark.Apps;

/// <summary>Which lifecycle events <see cref="AppsBuilder.AddEntityEvents{T}"/> raises.</summary>
[Flags]
public enum EntityEvents
{
    Created = 1,
    Updated = 2,
    Deleted = 4,
    All = Created | Updated | Deleted,
}

/// <summary>
/// <c>{name}.created</c>: a <typeparamref name="T"/> was created through Spark. Thin (D31): the receiver reads the entity
/// through the API with its installation token, so it sees exactly what its rights allow.
/// </summary>
public sealed record EntityCreated<T>([property: Description("The document id.")] string Id) where T : class;

/// <summary><c>{name}.updated</c>: a <typeparamref name="T"/> was saved through Spark (an edit, a revert or a restore).</summary>
public sealed record EntityUpdated<T>(
    [property: Description("The document id.")] string Id,
    [property: Description("The top-level attributes the edit changed, when the type keeps history; otherwise empty.")] IReadOnlyList<string> ChangedAttributes)
    where T : class;

/// <summary><c>{name}.deleted</c>: a <typeparamref name="T"/> was deleted through Spark.</summary>
public sealed record EntityDeleted<T>(
    [property: Description("The document id.")] string Id,
    [property: Description("True when the delete was a soft delete (the row still exists, marked deleted).")] bool Soft)
    where T : class;
