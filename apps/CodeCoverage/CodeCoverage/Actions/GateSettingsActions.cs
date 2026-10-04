using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Presentation rules for the coverage gate embedded in a <see cref="Repository"/>.
/// </summary>
/// <remarks>
/// ⚠️ <b>Presentation only.</b> A gate is never saved on its own — it lives inside its Repository's
/// document, so this class has no save path and <c>OnSaveAsync</c> here would never run. The rules
/// that must hold whatever a client posts are enforced in <see cref="RepositoryActions"/>, on the
/// object that actually saves. That split is deliberate and it does mean the two can drift: a change
/// to what <see cref="OnRefreshAsync"/> requires needs the matching change there.
/// <para>
/// Spark re-runs a root type's refresh hook while validating a save, which is what makes a
/// refresh-imposed rule real for a client that never calls <c>/refresh</c>. That re-derivation does
/// not descend into AsDetail rows, so nothing here is load-bearing for security.
/// </para>
/// </remarks>
public partial class GateSettingsActions : DefaultPersistentObjectActions<GateSettings>
{
    /// <summary>
    /// Shows the project target only when the comparison mode actually consumes one.
    /// </summary>
    /// <remarks>
    /// Establishes the complete state on both branches rather than only the one that changed: each
    /// invocation is handed a freshly scaffolded object, never the result of the last one, so an
    /// `if (fixed) show` with no `else` would leave the field visible forever after one visit.
    /// </remarks>
    public override Task OnRefreshAsync(SparkRefreshArgs<GateSettings> args)
    {
        ShapeForMode(args.PersistentObject);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The project target on the form only when the mode consumes it, through the runtime
    /// <c>ShowedOn</c> (#264, G-Q3). The model says <c>showedOn: Query</c>, so a new gate (auto by
    /// default) starts without it; this shows it for <c>fixed</c>.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="RepositoryActions.OnLoadAsync"/>: a gate is embedded in its repository
    /// and never loaded on its own, so the load-time half of this rule has to run there. Without it a
    /// gate saved as <c>fixed</c> opened with its target hidden until the mode was touched.
    /// </remarks>
    internal static void ShapeForMode(PersistentObject obj)
    {
        var isFixed = string.Equals(
            obj[nameof(GateSettings.ProjectMode)].GetValue<string>(),
            ProjectComparison.Fixed,
            StringComparison.Ordinal);

        var target = obj[nameof(GateSettings.ProjectTarget)];
        // Off the form (not the grid) unless the mode consumes it.
        target.ShowedOn = isFixed ? EShowedOn.Query | EShowedOn.PersistentObject : EShowedOn.Query;
        target.IsRequired = isFixed;

        // ⚠️ ProjectThreshold is deliberately NOT toggled. It reads like an "auto"-only setting and
        // it is not: GateEvaluator judges `headRate >= baseRate - ProjectThreshold` in both modes,
        // where baseRate is the fixed target under "fixed". Hiding it would hide a number that is
        // still in force.
    }
}
