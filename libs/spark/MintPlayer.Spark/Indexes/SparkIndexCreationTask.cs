using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark;

/// <summary>
/// An <see cref="AbstractIndexCreationTask{TDocument}"/> that applies Spark's declared field options
/// automatically, instead of requiring the constructor to remember to call a generated method.
/// </summary>
/// <remarks>
/// <para>
/// The source generator emits the <c>Index(...)</c> calls that <c>[Search]</c> and
/// <c>DateTimeOffset</c> fields on a <c>[FromIndex]</c> projection require. Until this class existed,
/// it could only emit them into a <c>private void IndexSearchFields()</c> and <em>document</em> that
/// the constructor must call it — a generator can add members to a class but cannot add statements to
/// a hand-written constructor body. Nothing verified the call, and an uncalled private method raises
/// no compiler warning: declare the index <c>partial</c>, add <c>[Search]</c>, forget one line, and
/// the index deploys, reports healthy, returns correct row counts, and full-text search silently does
/// nothing. Deriving from this class instead makes the call structural.
/// </para>
/// <para>
/// ⚠️ The <c>DateTimeOffset</c> half is the more severe one: without its wrapper companion Corax
/// parks the whole index at <c>state=Error, entries=0</c> after a clean deploy.
/// </para>
/// <para>
/// ⚠️ <b>Do not add <c>StoreAllFields</c> here.</b> Storing a <c>DateTimeOffset</c> flattens it
/// through projection and loses the offset, which <c>CommitIndexShapeGuardTests</c> fails the build
/// over. It would also be a real <c>Fields</c> change on every index in every application at once —
/// the one way adopting this class could turn into a corpus-wide reindex.
/// </para>
/// </remarks>
public abstract class SparkIndexCreationTask<TDocument> : AbstractIndexCreationTask<TDocument>
{
    /// <inheritdoc />
    /// <remarks>
    /// The derived constructor has already run by the time this is called, so <c>Map</c> is assigned
    /// and <see cref="ConfigureSparkFields"/> can safely inspect what the constructor declared.
    /// </remarks>
    public override IndexDefinition CreateIndexDefinition()
    {
        ConfigureSparkFields();
        return base.CreateIndexDefinition();
    }

    /// <summary>
    /// Applies Spark's declared field options. The generator overrides this; hand-written indexes may
    /// too.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Implementations must be idempotent</b>, and the way to be idempotent is to skip a field
    /// that is already declared:
    /// <code>
    /// if (!IndexesStrings.ContainsKey(nameof(VCar.LicensePlate)))
    ///     Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
    /// </code>
    /// <c>Index(string, FieldIndexing)</c> is a <c>Dictionary.Add</c>, not an indexer assignment, so
    /// declaring a field twice throws <see cref="ArgumentException"/> out of
    /// <see cref="CreateIndexDefinition"/> — the application fails to start. That matters in two real
    /// situations: an index whose constructor still calls the generated method while this class also
    /// calls it, and any code that invokes <see cref="CreateIndexDefinition"/> more than once on one
    /// instance (a guard test does exactly that). The generator emits the guard for you.
    /// <para>
    /// ⚠️ Use the <c>nameof</c> string overload, never <c>Index(x =&gt; x.Field, …)</c>.
    /// <c>AbstractIndexCreationTask&lt;T&gt;</c> is <c>AbstractIndexCreationTask&lt;T, T&gt;</c>, so the
    /// lambda binds against the <em>document</em>, not the projection — it cannot name a computed
    /// field, a <c>{Name}Sort</c> companion or a <c>{Name}Raw</c> wrapper, which are exactly the
    /// fields that need configuring. The guard above cannot see a lambda-declared field either, so
    /// mixing the two surfaces later as an <c>IndexCompilationException</c> about a duplicate key.
    /// </para>
    /// </remarks>
    protected virtual void ConfigureSparkFields()
    {
    }
}

/// <summary>
/// The multi-map counterpart of <see cref="SparkIndexCreationTask{TDocument}"/>.
/// </summary>
/// <remarks>
/// ⚠️ <typeparamref name="TReduceResult"/> is the <b>reduce result</b>, not a mapped collection — a
/// multi-map index maps several collections and names none of them. Spark's catalog therefore records
/// such an index with no collection type, which means it can be resolved by an explicit
/// <c>indexName</c> binding but never becomes a collection's default index.
/// </remarks>
public abstract class SparkMultiMapIndexCreationTask<TReduceResult> : AbstractMultiMapIndexCreationTask<TReduceResult>
{
    /// <inheritdoc cref="SparkIndexCreationTask{TDocument}.CreateIndexDefinition" />
    public override IndexDefinition CreateIndexDefinition()
    {
        ConfigureSparkFields();
        return base.CreateIndexDefinition();
    }

    /// <inheritdoc cref="SparkIndexCreationTask{TDocument}.ConfigureSparkFields" />
    protected virtual void ConfigureSparkFields()
    {
    }
}
