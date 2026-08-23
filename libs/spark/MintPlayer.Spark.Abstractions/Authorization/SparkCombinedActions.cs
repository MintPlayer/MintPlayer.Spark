namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The action names that stand for several actions at once, so <c>QueryReadEditNewDelete/Person</c>
/// is one line in <c>security.json</c> instead of five.
/// </summary>
/// <remarks>
/// <b>Expansion is symmetric.</b> A combined action means the same thing on a denial as on a grant:
/// <c>deny EditNewDelete/Car</c> denies Edit, New and Delete. It did not used to — expansion was
/// filtered to non-denied rights, so a combined denial denied the literal string and therefore
/// nothing, and the loader refused that shape rather than fixing it. Symmetric syntax with
/// asymmetric semantics is a trap whichever way it is documented.
/// </remarks>
public static class SparkCombinedActions
{
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EditNew"] = ["Edit", "New"],
        ["EditNewDelete"] = ["Edit", "New", "Delete"],
        ["NewDelete"] = ["New", "Delete"],
        ["QueryRead"] = ["Query", "Read"],
        ["QueryReadEdit"] = ["Query", "Read", "Edit"],
        ["QueryReadEditNew"] = ["Query", "Read", "Edit", "New"],
        ["QueryReadEditNewDelete"] = ["Query", "Read", "Edit", "New", "Delete"],
        ["ReadEdit"] = ["Read", "Edit"],
        ["ReadEditNew"] = ["Read", "Edit", "New"],
        ["ReadEditNewDelete"] = ["Read", "Edit", "New", "Delete"],
    };

    /// <summary>Every recognised combined action name.</summary>
    public static IReadOnlyCollection<string> Names => Table.Keys;

    /// <summary>
    /// The actions <paramref name="action"/> stands for, or just itself when it stands for nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// A table lookup on the whole action, never a prefix test. <c>StartsWith</c> would turn
    /// <c>NewDeleteAttachment</c> into <c>New</c> + <c>DeleteAttachment</c>, and the right the
    /// author actually wrote would vanish.
    /// </remarks>
    public static IReadOnlyList<string> Expand(string action)
        => Table.TryGetValue(action, out var actions) ? actions : [action];

    /// <summary>Whether <paramref name="action"/> is one of the combined names.</summary>
    public static bool IsCombined(string action) => Table.ContainsKey(action);
}
