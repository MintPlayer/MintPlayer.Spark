using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Models;

/// <summary>
/// One action of the composed catalogue (#467, D7): a built-in (New, Edit, Delete) or a custom action,
/// as the layers of <c>actions.json</c> compose it, with its text resolved from translations.json.
/// </summary>
public sealed class ActionDefinition
{
    /// <summary>The action's name, as the first layer declaring it spells it. Rights name it <c>{Name}/{Type}</c>.</summary>
    public required string Name { get; init; }

    /// <summary><c>actions.{Name}.label</c>, or the explicit <c>label</c> key; the humanized name when untranslated.</summary>
    public required TranslatedString Label { get; init; }

    /// <summary><c>actions.{Name}.description</c>, or the explicit key; null when untranslated.</summary>
    public TranslatedString? Description { get; init; }

    public string? Icon { get; init; }

    /// <summary>Where the action is shown: <c>"detail"</c>, <c>"query"</c> or <c>"both"</c> (lower case).</summary>
    public string ShowedOn { get; init; } = "both";

    /// <summary>
    /// The selection a query invocation must have, as a cardinality expression over the selected row
    /// count: <c>"=1"</c>, <c>"&gt;0"</c>, <c>"&lt;=5"</c>, <c>"1&lt;X&lt;5"</c>. Null requires nothing.
    /// Ignored on the detail page, which acts on the object it shows.
    /// </summary>
    public string? SelectionRule { get; init; }

    /// <summary>Whether the client refreshes after the action completes.</summary>
    public bool RefreshOnCompleted { get; init; }

    /// <summary>
    /// The confirmation asked before the action runs: <c>actions.{Name}.confirmation</c>, or the
    /// explicit <c>confirmation</c> key. Null asks nothing. May contain <c>{count}</c>, the number of
    /// rows the action is about to act on.
    /// </summary>
    public TranslatedString? Confirmation { get; init; }

    /// <summary>
    /// How prominently, and how warily, to present the action: <c>"primary"</c>, <c>"secondary"</c>,
    /// <c>"danger"</c>, <c>"warning"</c>. Null renders the neutral default. Presentation, never
    /// authorization; pair <c>"danger"</c> with a confirmation, because the colour warns and the
    /// prompt is what actually prevents the accident.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>Display order (lower = first); equal offsets keep the catalogue's order.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// The client method the action needs in the browser (<c>provideSparkClientMethods</c>), e.g.
    /// <c>"webauthn.create"</c>. The client shows the action disabled, with a reason, when the method
    /// is not registered or reports itself unsupported. Advisory only: the action still has to treat
    /// a cancelled client-method step gracefully.
    /// </summary>
    public string? RequiresClient { get; init; }

    /// <summary>The layer that declared the action.</summary>
    public required string DeclaredBy { get; init; }

    /// <summary>Each stated property and the layer that set it.</summary>
    public required IReadOnlyDictionary<string, string> Sources { get; init; }

    /// <summary>Whether this is one of the framework's own actions (New, Edit, Delete), which run through their own endpoints.</summary>
    public bool IsBuiltIn => SparkDefaultActions.IsDefault(Name);

    public bool IsShowedOnQuery => ShowedOn is "query" or "both";

    public bool IsShowedOnDetail => ShowedOn is "detail" or "both";
}

/// <summary>The composed action catalogue: every action no layer removed, in catalogue order.</summary>
public sealed class ActionsCatalogue
{
    public ActionsCatalogue(IReadOnlyList<ActionDefinition> actions, IReadOnlyList<SparkActionsConflict> conflicts)
    {
        Actions = actions;
        Conflicts = conflicts;
    }

    public static ActionsCatalogue Empty { get; } = new([], []);

    public IReadOnlyList<ActionDefinition> Actions { get; }

    /// <summary>Properties two libraries state differently. Reported at startup; the later library wins.</summary>
    public IReadOnlyList<SparkActionsConflict> Conflicts { get; }

    /// <summary>The action named <paramref name="name"/> (case-insensitive), or null when no layer declares it or one removed it.</summary>
    public ActionDefinition? Find(string? name)
        => name is null ? null : Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}
