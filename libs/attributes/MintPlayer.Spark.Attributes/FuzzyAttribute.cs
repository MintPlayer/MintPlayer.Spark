namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Tunes Spark's typo-tolerant search for one index field (commerce X18).
/// <para>
/// <b>Search is fuzzy by default</b> (owner decision 2026-10-19). Every index with a searchable field
/// (<c>FieldIndexing.Search</c>: a <c>[Search]</c> property, its generated <c>{Name}Search</c> companion, a
/// <c>TranslatedString</c>'s per-language companions, or a hand-written <c>Index(x =&gt; x.F, FieldIndexing.Search)</c>)
/// runs on the <b>Lucene</b> engine, and the query page's and lookups' search matches those fields with
/// <c>fuzzy(...)</c> as well as the substring wildcard, so <c>volkswagon</c> finds <c>Volkswagen</c>. Corax, the
/// server default on Community and Developer licences, refuses a fuzzy query at runtime
/// (<c>InvalidQueryException: Method 'Fuzzy' is not supported</c>, measured on 7.2.5), which is why the engine
/// follows the fields. Indexes without a searchable field keep the server default.
/// </para>
/// <list type="bullet">
/// <item><c>[Fuzzy(false)]</c> opts a searchable field <b>out</b>: Spark's search matches it by substring only.
/// The index still runs Lucene.</item>
/// <item><c>[Fuzzy]</c> on a field that is <b>not</b> searchable opts it in to Lucene for the author's own
/// <c>Fuzzy(...)</c> queries (a whole-value fuzzy match on a plain field). Spark's own search does not add fuzzy
/// clauses for it.</item>
/// </list>
/// <para>Where the marker is read:</para>
/// <list type="bullet">
/// <item>On a <c>[GenerateIndex]</c> entity property: the generator copies it onto the projection (and the
/// search companion), and sets <c>SearchEngineType = SearchEngineType.Lucene</c> on the index itself.</item>
/// <item>On a hand-written <c>[FromIndex]</c> projection property, or on the document type a hand-written index
/// maps. An opt-out on <c>Name</c> also covers its <c>NameSearch</c> companion.</item>
/// <item>On the result type of a library-declared index (<see cref="SparkIndexMapAttribute"/>).</item>
/// </list>
/// <para>
/// An index whose constructor pins <c>SearchEngineType.Corax</c> keeps it: Spark's search is then not fuzzy on it.
/// Only an explicit <c>[Fuzzy]</c> opt-in against a Corax pin stops startup, since every fuzzy query on it would
/// throw. Changing the engine is a definition change: RavenDB rebuilds the index side by side, and Spark resets
/// side by side any Lucene-declared index that still runs another engine. Analyzer SPARK063 flags a
/// <c>Fuzzy(...)</c> query against an index with no searchable or <c>[Fuzzy]</c> field.
/// </para>
/// </summary>
/// <param name="enabled"><see langword="false"/> to opt a searchable field out of fuzzy matching.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FuzzyAttribute(bool enabled = true) : Attribute
{
    /// <summary>Whether the field is matched fuzzily. <see langword="false"/> is the opt-out.</summary>
    public bool Enabled { get; } = enabled;
}
