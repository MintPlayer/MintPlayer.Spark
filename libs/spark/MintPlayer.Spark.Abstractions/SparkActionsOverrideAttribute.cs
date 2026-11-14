namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// States that <see cref="ActionsType"/> takes over <see cref="EntityType"/>'s actions from the actions class of a
/// referenced library it derives from (#660 G17). Emitted by the source generator, never written by hand.
/// </summary>
/// <remarks>
/// Actions classes are found by name (<c>{Entity}Actions</c>), and two non-abstract classes with that name make the
/// entity's actions ambiguous, which refuses resolution. A library that ships <c>LogActions</c> and an app that
/// specialises it as its own <c>LogActions : Library.LogActions</c> would hit exactly that. The generator sees the
/// derivation at compile time and records it here; the resolver then picks the most-derived class, but only when
/// every other match is one of its base classes. Unrelated duplicates still refuse.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SparkActionsOverrideAttribute : Attribute
{
    public SparkActionsOverrideAttribute(Type entityType, Type actionsType)
    {
        EntityType = entityType;
        ActionsType = actionsType;
    }

    /// <summary>The entity whose actions are taken over.</summary>
    public Type EntityType { get; }

    /// <summary>The app's (or a downstream library's) actions class, deriving from the library's.</summary>
    public Type ActionsType { get; }
}
