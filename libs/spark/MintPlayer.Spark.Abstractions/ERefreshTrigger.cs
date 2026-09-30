using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// When changing an attribute asks the server to reshape the object (see
/// <see cref="EntityAttributeDefinition.TriggersRefresh"/>). Serialized PascalCase, like
/// <see cref="EReferenceDisplayType"/>.
/// </summary>
/// <remarks>
/// A <i>discrete</i> editor is one where every change is a committed one: a lookup (any attribute with a
/// <c>lookupReferenceType</c>), a reference, a boolean, a date, a datetime, an enum or a colour.
/// Everything else is <i>free text</i>, where a refresh per keystroke would be unacceptable.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ERefreshTrigger
{
    /// <summary>
    /// No refresh — the same as leaving <c>triggersRefresh</c> out.
    /// </summary>
    None,

    /// <summary>
    /// Free text refreshes on blur (or on save); a discrete editor refreshes immediately on change.
    /// </summary>
    Auto,

    /// <summary>
    /// Refreshes on every change. On free text the change is debounced (300 ms) and flushed
    /// immediately by blur or save; on a discrete editor it is immediate.
    /// </summary>
    ValueChanged,

    /// <summary>
    /// Refreshes on blur (or on save). A discrete editor has no meaningful blur, so there it acts as
    /// <see cref="ValueChanged"/> — and <c>--spark-verify-model</c> warns about the declaration.
    /// </summary>
    Blur,
}
