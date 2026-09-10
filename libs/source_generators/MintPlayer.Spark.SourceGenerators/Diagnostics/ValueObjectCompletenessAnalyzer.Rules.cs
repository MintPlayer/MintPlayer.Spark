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
    /// ⚠️ <b>The location is always inside the analyzed compilation, and that is load-bearing.</b>
    /// Roslyn's analyzer driver <b>discards</b> any diagnostic whose location lives in a syntax tree
    /// the compilation does not contain — measured: a two-project fixture reported zero diagnostics
    /// where the identical single-project fixture reported one. Since the offending type usually
    /// lives in an entity library, reporting at the type's own declaration meant that in the IDE,
    /// where a project reference is a <c>CompilationReference</c> with real source locations, the
    /// diagnostic <b>vanished entirely</b>. It survived only under <c>dotnet build</c>, where the
    /// library is a .dll, the type has no source location, and <c>Location.None</c> is not dropped.
    /// </para>
    /// <para>
    /// So the location falls back to the <c>SparkContext</c> property that reaches the type — the
    /// nearest thing this compilation owns. The IDE now shows the error, and <c>dotnet build</c>
    /// prints a file and line instead of a bare <c>CSC : error</c>. The message still names the
    /// fully-qualified type, because the location no longer identifies it.
    /// </para>
    /// <para>
    /// A code fix rides on this: a provider is only ever offered a diagnostic the IDE could attach
    /// to a document, so the property <c>SparkOffendingType</c> carries the type's metadata name and
    /// the fix resolves the declaration through the solution. See
    /// <c>ValueObjectCompletenessCodeFixProvider</c> in MintPlayer.Spark.LibraryGenerators.
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
