using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

public sealed partial class MapControllersAnalyzer
{
    /// <summary>
    /// Warning rather than error: measured, a second <c>MapControllers()</c> reuses MVC's single
    /// endpoint data source, so the route table is correct — what is lost is Spark's authorization
    /// and antiforgery scoping, which the message must say rather than merely naming a replacement.
    /// Suppressible, so an app that wants the raw call makes that a reviewable decision instead of
    /// an unknowing one.
    /// </summary>
    internal static readonly DiagnosticDescriptor BareMapControllersRule = new(
        id: "SPARK010",
        title: "MapControllers() bypasses Spark's rules",
        messageFormat: "'MapControllers()' mounts controllers outside Spark, so Spark's authorization and antiforgery rules do not apply to them. Call 'spark.UseControllers()' during AddSpark instead.",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Controllers mapped with the framework's own MapControllers() are invisible to Spark, which cannot scope its antiforgery gate to endpoints it does not know about and cannot authorize an action against a security.json right. spark.UseControllers() mounts the same controllers inside Spark's pipeline. This is a compile-time rule because the call leaves no runtime trace to check.");
}
