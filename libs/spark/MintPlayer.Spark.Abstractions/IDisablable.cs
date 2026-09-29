namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Something whose actions can be withheld: a persistent object or a query result.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> member in Spark that knows how to disable an action (#460, D13). The
/// framework hands an <see cref="IDisablable"/> to the actions class's <c>OnDisableActionsAsync</c>
/// hook, and that hook is the single source of truth: the framework calls it when a page loads (the
/// answer travels back as <c>DisabledActions</c>) and again when any action is submitted, built-in
/// (<c>Edit</c>, <c>Save</c>, <c>Delete</c>, <c>New</c>) or custom, refusing a disabled one with
/// <c>403</c>.
/// </para>
/// <para>
/// Do not downcast the target. At load it may be the object being returned; at submit it is a
/// framework collector, because what the hook judges then is the stored state, not what the client
/// posted. Read the context instead, which says what the target is.
/// </para>
/// </remarks>
public interface IDisablable
{
    /// <summary>
    /// Withholds one or more actions, by name (case-insensitive). Idempotent and additive, so
    /// separate concerns can each withhold what they own without coordinating. Blank names are ignored.
    /// </summary>
    void DisableActions(params string[] actionNames);
}
