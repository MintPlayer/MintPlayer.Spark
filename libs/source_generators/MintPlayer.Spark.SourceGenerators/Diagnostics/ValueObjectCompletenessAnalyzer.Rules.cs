using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

public sealed partial class ValueObjectCompletenessAnalyzer
{
    /// <summary>
    /// A type the model reaches as an embedded object, that nobody marked.
    /// </summary>
    /// <remarks>
    /// The marker is what gives an embedded row a stable key, and the key is what lets a save match
    /// stored rows against incoming ones. Without it a save cannot tell an edited row from a deleted
    /// one plus a new one, so the row type's <c>New</c>/<c>Edit</c>/<c>Delete</c> rights cannot be
    /// applied and read-only fields cannot be preserved. Forgetting the attribute is therefore not a
    /// style problem; it silently removes a guarantee.
    /// <para>
    /// ⚠️ <b>Error, and reported without a location on purpose.</b> Under <c>dotnet build</c> a
    /// referenced project arrives as a .dll, so a type declared in an entity library has no source
    /// location in the application's compilation at all — measured: zero
    /// <c>DeclaringSyntaxReferences</c>, <c>Locations[0].Kind == MetadataFile</c>, null path. A
    /// diagnostic reported there renders as a bare <c>CSC : error</c>. That is fine: <b>the severity
    /// is the enforcement and the location is a convenience.</b> The message therefore names the
    /// fully-qualified type, because the message is all the developer gets.
    /// </para>
    /// <para>
    /// In the IDE the same analyzer receives a <c>CompilationReference</c> and a real location, so
    /// the squiggle lands on the class — but nothing depends on that.
    /// </para>
    /// </remarks>
    internal static readonly DiagnosticDescriptor MissingValueObjectRule = new(
        id: "SPARK017",
        title: "Embedded type is missing [ValueObject]",
        messageFormat: "'{0}' is reached from the Spark context as an embedded object, so it needs a row key — decorate it with [ValueObject] and declare it 'partial'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Types reachable from a SparkContext's IRavenQueryable properties are walked, and every complex type embedded along the way must carry [ValueObject] so the generator can give it a stable row key. Rows are matched by that key when a parent is saved; without one the save cannot distinguish an edit from a delete plus a create, which is what the row type's New, Edit and Delete rights are decided from.");
}
