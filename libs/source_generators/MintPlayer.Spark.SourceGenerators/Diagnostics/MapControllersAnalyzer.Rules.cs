using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

public sealed partial class MapControllersAnalyzer
{
    /// <summary>
    /// Warning rather than error: measured, a second <c>MapControllers()</c> reuses MVC's single
    /// endpoint data source, so the route table is correct.
    /// <para>
    /// ⚠️ The message used to say authorization did not apply, which overstated it (#327 §9.5).
    /// <c>[SparkAuthorize]</c> is an endpoint filter carried by the action itself, so it runs
    /// whichever call mounted the route — a controller mapped bare is still authorized. What is
    /// actually lost is narrower and easier to miss for exactly that reason: Spark scopes its
    /// antiforgery gate to the endpoints it mounted, so a bare-mapped controller falls outside it,
    /// and it sits at a stage of the pipeline Spark did not choose. Saying "authorization does not
    /// apply" sends the author looking for a missing attribute they already have, and — worse —
    /// implies that adding one fixes the antiforgery hole, which it does not.
    /// </para>
    /// Suppressible, so an app that wants the raw call makes that a reviewable decision instead of
    /// an unknowing one.
    /// </summary>
    internal static readonly DiagnosticDescriptor BareMapControllersRule = new(
        id: "SPARK010",
        title: "MapControllers() mounts controllers outside Spark's pipeline",
        messageFormat: "'MapControllers()' mounts controllers outside Spark, so Spark's antiforgery gate does not cover them and they run at a pipeline stage Spark did not choose. ([SparkAuthorize] still applies — it travels with the action.) Call 'spark.UseControllers()' during AddSpark instead.",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Spark scopes its antiforgery gate to the endpoint paths it mounted, so controllers mapped with the framework's own MapControllers() are outside it, and they are ordered wherever the bare call sits rather than where Spark places its own. [SparkAuthorize] is unaffected: it is an endpoint filter on the action and runs either way — which is what makes this easy to miss, since the controller looks protected. spark.UseControllers() mounts the same controllers inside Spark's pipeline. This is a compile-time rule because the call leaves no runtime trace to check.");
}
