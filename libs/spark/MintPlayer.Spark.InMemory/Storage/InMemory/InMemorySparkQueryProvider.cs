using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// The in-memory store's half of a Spark query: none. Its collections are in-memory sequences already, so every query
/// takes the pipeline's LINQ-to-objects path (the one a custom query's <c>AsQueryable()</c> takes): filters, sorting,
/// paging and search run in Spark itself. The store-query hooks are never reached.
/// </summary>
[Register(typeof(ISparkQueryProvider), ServiceLifetime.Scoped)]
internal sealed partial class InMemorySparkQueryProvider : ISparkQueryProvider
{
    [Inject] private readonly ISparkSession session;

    public bool IsStoreQuery(IQueryable source) => false;

    /// <summary>A <see cref="SparkView{TRow}"/> runs as LINQ to objects (#536 D32); the store has no native indexes.</summary>
    public IQueryable OpenIndex(Type entityType, Type indexType, bool narrowToCollection)
        => typeof(ISparkView).IsAssignableFrom(indexType)
            ? InMemoryViewRenderer.Render((ISparkView)Activator.CreateInstance(indexType)!, session)
            : throw new NotSupportedException(
                $"The in-memory store has no native indexes: {indexType.Name} cannot be queried. Declare it as a SparkView<TRow>, or query {entityType.Name}'s collection.");

    public IQueryable Project(IQueryable source, Type resultType) => source;

    public IQueryable Include(IQueryable source, IReadOnlyCollection<string> paths) => source;

    public Expression In(Expression member, IReadOnlyList<string> values)
        => Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], Expression.Constant(values), member);

    public IReadOnlySet<string>? IndexedFields(IQueryable source) => null;

    public IQueryable? Search(IQueryable source, IReadOnlyList<PropertyInfo> properties, IReadOnlyList<string> words) => null;

    public Task<int> CountAsync(IQueryable source, CancellationToken cancellationToken)
        => Task.FromResult(source.Cast<object>().Count());

    public Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<object>>([.. source.Cast<object>()]);
}
