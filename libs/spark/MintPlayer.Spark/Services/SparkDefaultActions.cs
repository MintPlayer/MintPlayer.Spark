using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Models;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The built-in actions every type offers when the caller holds the matching right — <c>New</c> and
/// <c>Delete</c> — expressed as catalogue entries with the same shape as a custom action (#460, D18).
/// </summary>
/// <remarks>
/// <para>
/// Defaults: <c>New</c> has no selection rule and <c>Delete</c> is <c>&gt;0</c>, both
/// <c>showedOn: "both"</c>. An application overrides either by adding an entry with the same name to
/// <c>customActions.json</c>; it needs no C# class, because the framework implements both. Every field
/// the override states wins, and a field it leaves out (<c>displayName</c>, <c>icon</c>,
/// <c>description</c>, <c>selectionRule</c>, <c>confirmationMessageKey</c>, <c>variant</c>) keeps the
/// default. <c>showedOn</c> and <c>offset</c> are always the entry's, since the file cannot say "absent"
/// for them and their file defaults (<c>"both"</c>, <c>0</c>) equal the built-in ones. To drop
/// Delete's rule, write <c>"selectionRule": ""</c>.
/// </para>
/// <para>
/// ⚠️ Rights are the ordinary ones — <c>New/T</c> and <c>Delete/T</c> — not an action right per name.
/// An <see cref="Actions.ICustomAction"/> class named <c>New</c> or <c>Delete</c> is never executed:
/// the name belongs to the framework.
/// </para>
/// </remarks>
public static class SparkDefaultActions
{
    public const string New = "New";
    public const string Delete = "Delete";

    /// <summary>The largest selection a row-taking action, built-in or custom, accepts in one request.</summary>
    public const int MaxSelectedItems = 200;

    /// <summary>Whether <paramref name="name"/> is one of the built-in action names (case-insensitive).</summary>
    public static bool IsDefault(string? name)
        => string.Equals(name, New, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Delete, StringComparison.OrdinalIgnoreCase);

    private static CustomActionDefinition Builtin(string name) => name switch
    {
        New => new CustomActionDefinition
        {
            DisplayName = TranslatedString.Create("New", "Nouveau", "Nieuw"),
            Icon = "plus-lg",
            ShowedOn = "both",
            Variant = "primary",
        },
        Delete => new CustomActionDefinition
        {
            DisplayName = TranslatedString.Create("Delete", "Supprimer", "Verwijderen"),
            Icon = "trash",
            ShowedOn = "both",
            SelectionRule = ">0",
            Variant = "danger",
            ConfirmationMessageKey = "common.confirmDeleteSelected",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a default action."),
    };

    /// <summary>
    /// The effective definition of a built-in action: the default, with whatever the application's
    /// <c>customActions.json</c> entry of the same name states laid over it.
    /// </summary>
    public static CustomActionDefinition Resolve(string name, CustomActionsConfiguration? configuration)
    {
        var canonical = string.Equals(name, New, StringComparison.OrdinalIgnoreCase) ? New
            : string.Equals(name, Delete, StringComparison.OrdinalIgnoreCase) ? Delete
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Not a default action.");

        var definition = Builtin(canonical);
        var entry = configuration?
            .FirstOrDefault(kv => string.Equals(kv.Key, canonical, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (entry is null)
            return definition;

        return new CustomActionDefinition
        {
            DisplayName = entry.DisplayName ?? definition.DisplayName,
            Icon = entry.Icon ?? definition.Icon,
            Description = entry.Description ?? definition.Description,
            ShowedOn = entry.ShowedOn,
            SelectionRule = entry.SelectionRule ?? definition.SelectionRule,
            RefreshOnCompleted = entry.RefreshOnCompleted,
            ConfirmationMessageKey = entry.ConfirmationMessageKey ?? definition.ConfirmationMessageKey,
            Variant = entry.Variant ?? definition.Variant,
            Offset = entry.Offset,
        };
    }

    /// <summary>Whether <paramref name="showedOn"/> offers the action on a query (<c>"query"</c> or <c>"both"</c>).</summary>
    public static bool IsShowedOnQuery(string? showedOn)
        => string.Equals(showedOn, "query", StringComparison.OrdinalIgnoreCase)
            || string.Equals(showedOn, "both", StringComparison.OrdinalIgnoreCase);
}
