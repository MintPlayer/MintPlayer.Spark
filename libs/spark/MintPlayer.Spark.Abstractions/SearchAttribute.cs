namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks a property as full-text searchable in a generated index, and — inseparably — gives it a
/// companion sort field.
/// <para>
/// The generated index indexes the property itself with <c>FieldIndexing.Search</c> and emits a second
/// projection property named <c>{PropertyName}Sort</c>, fed the identical value and decorated
/// <see cref="IgnorePropertyAttribute"/>.
/// </para>
/// <para><strong>The companion is not redundant; it is the repair.</strong> A field indexed
/// <c>Search</c> is analyzed, so <c>Volkswagen Golf GTI</c> is stored as three separate terms. Ordering
/// on such a field is meaningless and equality or prefix matching against it behaves as a full-text
/// match rather than a comparison. The companion carries no <c>Index(...)</c> call at all, which leaves
/// it at RavenDB's default indexing — a single, un-tokenized term — and therefore sortable and
/// exact-matchable. Marking a field searchable and giving it a sort companion are one decision, which
/// is why one attribute does both.</para>
/// <para>
/// The companion is hidden from the Spark model, not from code. It has no model attribute, no label and
/// no <c>AttributeNames</c> constant, but it remains an ordinary property on the projection class, so
/// <c>x.Model == value</c> and <c>x.ModelSort.StartsWith(value)</c> are both available in LINQ.
/// </para>
/// <para>
/// Sorting is redirected automatically: callers, query JSON and the <c>?sortBy=</c> override all keep
/// naming the display property, and the framework orders by the companion via the attribute's
/// <c>SortExpression</c>. Nothing needs to know the companion exists.
/// </para>
/// <para>
/// Valid on <c>string</c>, <c>string[]</c> / <c>IEnumerable&lt;string&gt;</c> and
/// <see cref="TranslatedString"/>; anything else is reported as a diagnostic rather than silently
/// producing an analyzed non-text field. Composes with <see cref="IgnorePropertyAttribute"/> — the field
/// is then indexed and searchable while staying out of the model. <c>DateTimeOffset</c> properties get a
/// sort companion automatically and need no attribute.
/// </para>
/// <para>
/// Only needed when values can contain spaces. A code, an identifier or an enum-backed string has
/// nothing to gain from it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SearchAttribute : Attribute;
