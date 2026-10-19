namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks an index field as queried with <c>Fuzzy(...)</c>, which makes Spark deploy the index that carries it
/// on the <b>Lucene</b> search engine (commerce X18, reference-data RD8b).
/// <para>
/// Corax, the server default on Community and Developer licences, refuses a fuzzy query at runtime
/// (<c>InvalidQueryException: Method 'Fuzzy' is not supported</c>, measured on 7.2.5). The engine is chosen per
/// index on every licence, so the marker turns it on for exactly the indexes that need it; every other index keeps
/// the server default.
/// </para>
/// <para>Where it applies:</para>
/// <list type="bullet">
/// <item>On a <c>[GenerateIndex]</c> entity property: the generated index sets
/// <c>SearchEngineType = SearchEngineType.Lucene</c> itself, and the marker is copied onto the projection.</item>
/// <item>On a hand-written <c>[FromIndex]</c> projection property, or on the document type a hand-written index
/// maps: Spark's index registration sets the engine on that index before deploying it. An index whose constructor pins <c>SearchEngineType.Corax</c> while a
/// projection marks a field fuzzy fails startup instead, since every fuzzy query on it would throw.</item>
/// <item>On the result type of a library-declared index (<see cref="SparkIndexMapAttribute"/>): the index the
/// application's generator emits sets the engine itself.</item>
/// </list>
/// <para>
/// Changing the engine is a definition change: RavenDB rebuilds the index side by side, and Spark checks after
/// deployment that every fuzzy index actually runs Lucene, resetting it side by side when it does not. Analyzer
/// SPARK063 flags a <c>Fuzzy(...)</c> query against an index none of whose fields is marked.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FuzzyAttribute : Attribute;
