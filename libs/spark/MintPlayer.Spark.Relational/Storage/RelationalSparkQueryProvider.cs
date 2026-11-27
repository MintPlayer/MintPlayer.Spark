using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// The relational half of a Spark query (S2b): EF translates the pipeline's LINQ as it is. What is Spark's own: the
/// join-table rewrite (D26, at execution), id restrictions over typed keys, and search as a case-insensitive
/// <c>LIKE</c> (S10's first step).
/// </summary>
public sealed class RelationalSparkQueryProvider : ISparkQueryProvider
{
    private readonly ISparkSession session;

    public RelationalSparkQueryProvider(ISparkSession session) => this.session = session;

    private RelationalSparkSession Session => (RelationalSparkSession)session;

    public bool IsStoreQuery(IQueryable source) => source.Provider is IAsyncQueryProvider;

    /// <summary>A <c>SparkView&lt;TRow&gt;</c> is an SQL view (#536 D32); it reads one collection, so there is nothing to narrow.</summary>
    public IQueryable OpenIndex(Type entityType, Type indexType, bool narrowToCollection) => Session.ViewRows(indexType);

    public IQueryable Project(IQueryable source, Type resultType) => source;

    /// <summary>No navigation properties (D26): referenced rows are batch-loaded by id after the query.</summary>
    public IQueryable Include(IQueryable source, IReadOnlyCollection<string> paths) => source;

    /// <summary>
    /// <c>ids.Contains(member)</c> over the member's own type (a string or an <see cref="Abstractions.Id{T}"/>), the list
    /// a captured field so EF parameterizes it.
    /// </summary>
    public Expression In(Expression member, IReadOnlyList<string> values)
    {
        var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        var typed = values.Select(v => SparkIdValues.FromText(type, v)).OfType<object>().ToList();
        var body = Captured.Contains(type, typed, type == member.Type ? member : Expression.Convert(member, type));
        return body;
    }

    public IReadOnlySet<string>? IndexedFields(IQueryable source) => null;

    /// <summary>
    /// RavenDB's semantics: every word must occur in one field, and any field may match. Case-insensitive on both
    /// databases (lower on both sides). Non-string members are skipped.
    /// </summary>
    public IQueryable? Search(IQueryable source, IReadOnlyList<PropertyInfo> properties, IReadOnlyList<string> words)
    {
        var lowered = words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).ToList();
        var strings = properties.Where(p => p.PropertyType == typeof(string)).ToList();
        if (lowered.Count == 0 || strings.Count == 0)
            return null;

        var x = Expression.Parameter(source.ElementType, "x");
        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression? any = null;
        foreach (var property in strings)
        {
            var member = Expression.Property(Expression.Convert(x, property.DeclaringType!), property);
            Expression all = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));
            foreach (var word in lowered)
                all = Expression.AndAlso(all, Expression.Call(Expression.Call(member, toLower), contains, Captured.Value(word, typeof(string))));
            any = any is null ? all : Expression.OrElse(any, all);
        }

        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [source.ElementType], source.Expression,
            Expression.Quote(Expression.Lambda(any!, x)));
        return source.Provider.CreateQuery(where);
    }

    public Task<int> CountAsync(IQueryable source, CancellationToken cancellationToken)
        => (Task<int>)Generic(nameof(RelationalSparkSession.CountAsync), source.ElementType).Invoke(Session, [source, cancellationToken])!;

    public async Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken cancellationToken)
    {
        var task = (Task)Generic(nameof(RelationalSparkSession.ToListAsync), source.ElementType).Invoke(Session, [source, cancellationToken])!;
        await task;
        return [.. ((System.Collections.IEnumerable)task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)!).Cast<object>()];
    }

    private static MethodInfo Generic(string name, Type type)
        => typeof(RelationalSparkSession).GetMethods().Single(m => m.Name == name && m.IsGenericMethod).MakeGenericMethod(type);
}
