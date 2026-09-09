namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Declares that this type is a <b>value object</b>: it has no document of its own and is stored
/// embedded inside the document that owns it, typically as an element of a collection.
/// <para>
/// The marker is what makes the type a value object as far as Spark is concerned — the set is never
/// inferred. An earlier design computed it by walking the type graph and was abandoned: a generator
/// may only add a <see langword="partial"/> half to a type in its own compilation, and entities live
/// in libraries while the context that reaches them lives in the application, so the walk cannot
/// start where it must emit. Every substitute root answered a different question ("is this indexed?",
/// "is this reachable?") and keyed the wrong types.
/// </para>
/// <para>
/// Rows of an embedded collection need a stable key, because that is what lets a save tell an added
/// row from an edited one from a deleted one, and therefore what lets <c>New/X</c>, <c>Edit/X</c> and
/// <c>Delete/X</c> mean anything on the write path. So a decorated type must be
/// <see langword="partial"/>: unless it already has a key marked <see cref="ValueKeyAttribute"/>,
/// one is generated for it.
/// </para>
/// </summary>
/// <remarks>
/// A decorated type that is not <see langword="partial"/> is reported as SPARK016 rather than
/// silently skipped — a value object missing from the registry is unjudgeable at save time, which
/// under the fail-closed diff is a failure, not a no-op.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ValueObjectAttribute : Attribute
{
}
