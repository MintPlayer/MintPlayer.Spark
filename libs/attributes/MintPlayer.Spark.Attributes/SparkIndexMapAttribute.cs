namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Declares one map of a RavenDB index from a library, without the library shipping an index class (commerce
/// X19, reference-data Q-R6). The application's source generator emits the index class at build time.
/// <para>
/// Owner rule: RavenDB indexes always live in the application. A reusable plugin still needs indexes, so it
/// declares them as data and every host gets the same index without copying code. The map itself is an ordinary
/// expression in the library, so it is type-checked where it is written:
/// </para>
/// <code>
/// public static class TypeaheadIndexes
/// {
///     [SparkIndexMap("RefLocalities_Typeahead")]
///     public static Expression&lt;Func&lt;IEnumerable&lt;Place&gt;, IEnumerable&lt;TypeaheadEntry&gt;&gt;&gt; Places { get; } =
///         places =&gt; from place in places
///                   from segment in place.Segments
///                   select new TypeaheadEntry { Key = segment.Key, Tokens = segment.Tokens };
/// }
/// </code>
/// <para>
/// The member must be <c>static</c>, public on a public type, and typed
/// <c>Expression&lt;Func&lt;IEnumerable&lt;TSource&gt;, IEnumerable&lt;TResult&gt;&gt;&gt;</c>. Every map that names the
/// same index, from any number of libraries, becomes one <c>AddMap&lt;TSource&gt;</c> of a single multi-map index
/// <c>{IndexName} : SparkMultiMapIndexCreationTask&lt;TResult&gt;</c> in the application's <c>{RootNamespace}.Indexes</c>
/// namespace, so one family of libraries keeps one index. All of them must agree on <c>TResult</c>.
/// </para>
/// <para>
/// Field options come from <c>TResult</c>: <see cref="SearchAttribute"/> declares that field
/// <c>FieldIndexing.Search</c> (the map is hand-written, so there is no generated companion; map a second, plain
/// field to sort on), and <see cref="FuzzyAttribute"/> deploys the index on Lucene. The emitted class is
/// <c>partial</c> and calls <c>partial void OnInitialize()</c>, for host-specific additions.
/// </para>
/// <para>
/// The index is queried by name, never by type, since a library cannot see the application's class:
/// <c>session.Query&lt;TypeaheadEntry&gt;("RefLocalities/Typeahead")</c>. A map can only use what an expression
/// in the library can express; index helpers that are instance members of the index class
/// (<c>LoadDocument</c>, <c>MetadataFor</c>, <c>CreateField</c>) are out of reach.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
public sealed class SparkIndexMapAttribute : Attribute
{
    /// <param name="indexName">
    /// The index class name, e.g. <c>RefLocalities_Typeahead</c> (deployed as <c>RefLocalities/Typeahead</c>). Must
    /// be a valid C# identifier.
    /// </param>
    public SparkIndexMapAttribute(string indexName)
    {
        IndexName = indexName;
    }

    /// <summary>The index class name the map contributes to.</summary>
    public string IndexName { get; }

    /// <summary>
    /// Stores every field (<c>StoreAllFields(FieldStorage.Yes)</c>), so a projection can read computed values back.
    /// The index stores its fields when any of its maps asks for it.
    /// </summary>
    public bool StoreAllFields { get; set; }
}
