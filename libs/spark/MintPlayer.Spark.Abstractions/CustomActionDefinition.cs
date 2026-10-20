using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One action of the composed catalogue, as <c>POST /spark/actions/list</c> reports it for a type (#467, D7): a
/// built-in (New, Edit, Delete) or a custom action, already narrowed to what the caller may run. Text is resolved on
/// the server (D26). Commerce X20 item 2 made it a contract; it used to be a private record with lower-case members,
/// which serialized to the same names.
/// </summary>
/// <param name="Name">The action's name.</param>
/// <param name="Label"><c>actions.{name}.label</c>, or the humanized name when untranslated.</param>
/// <param name="Icon">The icon name, if any.</param>
/// <param name="Description"><c>actions.{name}.description</c>; null when untranslated.</param>
/// <param name="ShowedOn">Where the action is offered: <c>query</c>, <c>persistentObject</c> or <c>both</c>.</param>
/// <param name="SelectionRule">How many rows the action needs, e.g. <c>=1</c> or <c>&gt;0</c>.</param>
/// <param name="RefreshOnCompleted">Whether the client reloads after the action ran.</param>
/// <param name="Confirmation">The prompt to show before running; may contain <c>{count}</c>. Null: no prompt.</param>
/// <param name="Variant">Presentation only: <c>primary</c>, <c>secondary</c>, <c>danger</c>, <c>warning</c>.</param>
/// <param name="Offset">The sort position; equal offsets keep the catalogue's order.</param>
/// <param name="IsDefault"><see langword="true"/> for New, Edit and Delete; left out for a custom action.</param>
/// <param name="RequiresClient">The client method the action needs in the browser (e.g. <c>webauthn.create</c>); left out when none.</param>
[SparkContract]
public sealed record CustomActionDefinition(
    string Name,
    TranslatedString Label,
    string? Icon,
    TranslatedString? Description,
    string ShowedOn,
    string? SelectionRule,
    bool RefreshOnCompleted,
    TranslatedString? Confirmation,
    string? Variant,
    int Offset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequiresClient);
