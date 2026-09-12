namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Carries a value through a stored index field as a <strong>nested object</strong>, so RavenDB
/// stores it as an opaque sub-document instead of decomposing it into a typed scalar index field.
/// </summary>
/// <remarks>
/// <para>
/// This exists for exactly one reason: RavenDB converts a <see cref="System.DateTimeOffset"/> to its
/// UTC-equivalent <c>DateTime</c> whenever the value becomes a <em>scalar</em> index field, forcing
/// the offset to <c>+00:00</c>. The document keeps the offset; a projection served from a stored
/// index field does not, and the value is already flattened on the wire — so no serializer,
/// converter or deserialization hook can recover it. Upstream:
/// <see href="https://github.com/ravendb/ravendb/issues/17901"/>, open since 2023-12, both engines,
/// no fix in any 7.1.x or 7.2.x changelog.
/// </para>
/// <para>
/// A value nested inside a complex object escapes that conversion entirely, because a nested object
/// is stored as an opaque blittable sub-document and is never decomposed. Measured: a scalar field
/// loses its offset even at <c>FieldIndexing.No</c>, while a nested one keeps it on both Corax and
/// Lucene. <strong>Nesting is the mechanism, not the indexing mode.</strong>
/// </para>
/// <para>
/// The type name never reaches the server: RavenDB's expression-to-string converter erases type
/// names from object-creation expressions, so <c>new SparkIndexValue&lt;T&gt; { V = x }</c> is deployed as
/// <c>new { V = x }</c>. Class vs struct vs generic vs declaring assembly are therefore
/// indistinguishable to RavenDB, and no type metadata is stored — renaming this type changes nothing
/// in any index definition.
/// </para>
/// <para>
/// ⚠️ A field carrying one of these <strong>must</strong> be declared <c>FieldIndexing.No</c>. Without
/// it Corax deploys the index cleanly and then parks it at <c>state=Error, entries=0</c>
/// (<c>NotSupportedInCoraxException</c>), so every query against it returns nothing. Lucene is
/// unaffected, which means the failure only appears on the engine CI and production actually run.
/// </para>
/// <para>
/// The wrapper is <strong>additive</strong>: a <c>FieldIndexing.No</c> field cannot be filtered (on
/// Corax a range predicate faults the server) and its ordering is undefined, so the real typed field
/// stays for ordering and filtering. That costs nothing — RavenDB's UTC normalisation makes ordering
/// across mixed offsets chronologically correct.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The carried type, matching the source property exactly — including nullability and array-ness.
/// A mismatch still works but makes RavenDB inject a cast into the deployed map.
/// </typeparam>
public sealed class SparkIndexValue<T>
{
    /// <summary>
    /// The carried value. Deliberately terse: this name appears in every generated index definition
    /// and in the JSON of every stored row.
    /// </summary>
    public T V { get; set; } = default!;
}
