namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks a property as full-text searchable in a generated index, which gives it an analyzed
/// <c>{PropertyName}Search</c> companion.
/// <para>
/// The generated index keeps the property itself at RavenDB's default indexing — a single, un-tokenized
/// term, so equality, prefix matching and ordering on the name everyone uses keep working — and emits a
/// second projection property named <c>{PropertyName}Search</c>, fed the identical value, indexed with
/// <c>FieldIndexing.Search</c> and decorated <see cref="IgnorePropertyAttribute"/>
/// (<c>IndexNaming.SearchCompanion</c>).
/// </para>
/// <para><strong>Why the analyzed copy is the companion.</strong> A field indexed <c>Search</c> is
/// analyzed, so <c>Volkswagen Golf GTI</c> is stored as three separate terms; ordering on it is
/// meaningless and equality matches nothing. The roles used to be the other way round (the base field
/// analyzed, a <c>{Name}Sort</c> copy for ordering), which put the breakage on the field every filter,
/// sort and hand-written row filter names. Now the base field is normal and only the search path
/// (<c>QueryExecutor.ApplySearch</c>) knows the companion exists.
/// </para>
/// <para>
/// The companion is hidden from the Spark model, not from code. It has no model attribute, no label and
/// no <c>AttributeNames</c> constant, but it remains an ordinary property on the projection class, so
/// <c>x.Model == value</c> and <c>x.ModelSearch</c> are both available in LINQ.
/// </para>
/// <para>
/// A <c>{Name}Sort</c> companion still exists for a different reason: an <em>exact</em> field ordered
/// by ordinal gets a plain copy that <c>QueryExecutor.ResolveSortProperty</c> orders by
/// (<c>IndexNaming.SortCompanion</c>, SPARK005). It is not what this attribute emits.
/// </para>
/// <para>
/// Valid on <c>string</c>, <c>string[]</c> / <c>IEnumerable&lt;string&gt;</c> and
/// <see cref="TranslatedString"/>; anything else is reported as a diagnostic rather than silently
/// producing an analyzed non-text field. Composes with <see cref="IgnorePropertyAttribute"/> — the field
/// is then indexed and searchable while staying out of the model. <c>DateTimeOffset</c> properties get a
/// <c>{Name}Raw</c> wrapper automatically and need no attribute — that carries the offset RavenDB strips
/// when a value becomes a scalar index field.
/// </para>
/// <para>
/// Only needed when values can contain spaces. A code, an identifier or an enum-backed string has
/// nothing to gain from it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SearchAttribute : Attribute;
