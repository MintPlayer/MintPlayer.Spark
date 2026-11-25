using System.Linq.Expressions;

namespace MintPlayer.Spark.Abstractions.Views;

/// <summary>
/// What a view reads (#536 D32). <see cref="Set{T}"/> is a collection, and <see cref="Load{T}(string)"/> follows a
/// reference id to its row, or <see langword="null"/>. <c>Load</c> is the one join form: RavenDB has it natively
/// (<c>LoadDocument</c>), SQL makes it a scalar subquery on the key, and memory a lookup. A LINQ <c>join</c> has no
/// RavenDB map form, so a view declares none.
/// </summary>
/// <remarks>Only ever called inside a <see cref="SparkView{TRow}.Map"/> expression, which each provider rewrites.</remarks>
public interface ISparkViewSource
{
    IQueryable<T> Set<T>() where T : class;

    T? Load<T>(string? id) where T : class;

    T? Load<T>(Id<T>? id) where T : class;

    /// <summary>
    /// The stored id of <paramref name="row"/>, a row of a <see cref="Set{T}"/>: what a view binds to its row's <c>Id</c>
    /// without knowing how the entity types its id. RavenDB supplies a projection's id itself; elsewhere it is the key.
    /// </summary>
    string? IdOf<T>(T row) where T : class;
}

/// <summary>A view as the catalog and the providers see it, without its row type.</summary>
public interface ISparkView
{
    /// <summary>The name a query or a model's <c>indexName</c> binds it by.</summary>
    string Name { get; }

    /// <summary>The row the view yields; the projection its queries return.</summary>
    Type RowType { get; }

    /// <summary>The declaration, read once (<see cref="SparkView{TRow}.Definition"/>).</summary>
    LambdaExpression Definition { get; }

    /// <summary>The collection the view enumerates: the type of the first <c>Set&lt;T&gt;()</c> in the declaration.</summary>
    Type CollectionType { get; }

    /// <summary>Every entity type the view reads (<c>Set</c> and <c>Load</c>): the tables it depends on.</summary>
    IReadOnlyList<Type> ReadTypes { get; }

    /// <summary>The reduce, read once (<see cref="SparkView{TRow}.ReduceDefinition"/>), or <see langword="null"/> for a map-only view.</summary>
    LambdaExpression? ReduceDefinition { get; }
}

/// <summary>
/// A named projection declared once and rendered by each provider (#536 D32): a RavenDB static index, an SQL view
/// whose SQL EF writes, or LINQ to objects in memory. A view is queried like an index, by its name.
/// </summary>
/// <example>
/// <code>
/// public sealed class PeopleWithCompany : SparkView&lt;VPerson&gt;
/// {
///     public override Expression&lt;Func&lt;ISparkViewSource, IQueryable&lt;VPerson&gt;&gt;&gt; Map =&gt; s =&gt;
///         from p in s.Set&lt;Person&gt;()
///         select new VPerson { Id = p.Id, FullName = p.FirstName + " " + p.LastName, CompanyName = s.Load&lt;Company&gt;(p.Company)!.Name };
/// }
/// </code>
/// </example>
/// <remarks>
/// <list type="bullet">
/// <item>The shape is <c>Set&lt;T&gt;().Select(…)</c>, optionally with <c>Where</c> before the <c>Select</c>: RavenDB's map takes one source collection.</item>
/// <item>
/// A fan-out map yields a row per list item: <c>from x in s.Set&lt;T&gt;() from item in x.List select new TRow { … }</c>
/// (#536, <c>Likes_Count</c>). RavenDB fans out in the index, SQL Server reads the list column with <c>OPENJSON</c>,
/// PostgreSQL with <c>unnest</c>, memory with <c>SelectMany</c>; a missing list yields no rows everywhere. A fanned-out
/// row has no <c>Id</c> (<c>IdOf</c> binds one row per entity).
/// </item>
/// <item>
/// A <see cref="SparkView{TRow}.Reduce"/> groups the mapped rows and sums them (#536 S16): a RavenDB map-reduce index,
/// a <c>GROUP BY</c> view in SQL, a grouping in memory. Its shape is <c>group r by key into g select new TRow { … }</c>
/// with keys and <c>g.Sum(…)</c>/<c>g.Count()</c> only, and its output is the map's row type, as RavenDB requires.
/// A reduced row has no <c>Id</c>.
/// </item>
/// <item>Constants only: a captured variable would become a parameter, and a view body takes none.</item>
/// <item>Member access through <c>Load</c> yields <see langword="null"/> (or the default) for a missing reference on every provider.</item>
/// </list>
/// </remarks>
public abstract class SparkView<TRow> : ISparkView where TRow : class
{
    private Expression<Func<ISparkViewSource, IQueryable<TRow>>>? definition;
    private IReadOnlyList<Type>? readTypes;

    public virtual string Name => GetType().Name;

    public Type RowType => typeof(TRow);

    /// <summary>The declaration. Read it through <see cref="Definition"/>.</summary>
    public abstract Expression<Func<ISparkViewSource, IQueryable<TRow>>> Map { get; }

    /// <summary>
    /// <see cref="Map"/>, read once. An expression-bodied property builds a new tree, with new parameter instances, on
    /// every read, so a renderer that read the parameters and the body separately would match nothing (S5 finding 8).
    /// </summary>
    public Expression<Func<ISparkViewSource, IQueryable<TRow>>> Definition => definition ??= Map;

    LambdaExpression ISparkView.Definition => Definition;

    /// <summary>
    /// Groups the mapped rows (#536 S16), e.g.
    /// <c>rows =&gt; from r in rows group r by r.UserId into g select new TRow { UserId = g.Key, Points = g.Sum(x =&gt; x.Points) }</c>.
    /// <see langword="null"/> (the default) for a map-only view. Read it through <see cref="ReduceDefinition"/>.
    /// </summary>
    public virtual Expression<Func<IEnumerable<TRow>, IEnumerable<TRow>>>? Reduce => null;

    private Expression<Func<IEnumerable<TRow>, IEnumerable<TRow>>>? reduceDefinition;
    private bool reduceRead;

    /// <summary><see cref="Reduce"/>, read once (see <see cref="Definition"/>).</summary>
    public Expression<Func<IEnumerable<TRow>, IEnumerable<TRow>>>? ReduceDefinition
    {
        get
        {
            if (!reduceRead)
            {
                reduceDefinition = Reduce;
                reduceRead = true;
            }
            return reduceDefinition;
        }
    }

    LambdaExpression? ISparkView.ReduceDefinition => ReduceDefinition;

    public IReadOnlyList<Type> ReadTypes => readTypes ??= SparkViewRewriter.SourceCalls(Definition).Select(c => c.Type).Distinct().ToList();

    public Type CollectionType => SparkViewRewriter.SourceCalls(Definition).FirstOrDefault(c => c.IsSet).Type
        ?? throw new InvalidOperationException($"View {Name} reads no collection: its declaration must start from Set<T>().");
}

/// <summary>
/// Replaces the <see cref="ISparkViewSource"/> calls in a view's declaration; each provider says what <c>Set</c> and
/// <c>Load</c> become.
/// </summary>
public abstract class SparkViewRewriter : ExpressionVisitor
{
    private readonly ParameterExpression source;

    protected SparkViewRewriter(LambdaExpression definition) => source = definition.Parameters[0];

    /// <summary>What <c>Set&lt;<paramref name="type"/>&gt;()</c> becomes.</summary>
    protected abstract Expression RenderSet(Type type);

    /// <summary>What <c>Load&lt;<paramref name="type"/>&gt;(id)</c> becomes; <paramref name="id"/> is already rewritten.</summary>
    protected abstract Expression RenderLoad(Type type, Expression id);

    /// <summary>What <c>IdOf(row)</c> becomes; <paramref name="row"/> is already rewritten.</summary>
    protected virtual Expression RenderIdOf(Expression row)
        => throw new NotSupportedException($"{GetType().Name} cannot render IdOf outside a row's Id binding.");

    /// <summary>Whether <paramref name="node"/> is a call on the view's source.</summary>
    protected bool IsSourceCall(Expression? node, string method)
        => node is MethodCallExpression call && call.Object == source && call.Method.Name == method;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Set))
            return RenderSet(node.Method.GetGenericArguments()[0]);
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Load))
            return RenderLoad(node.Method.GetGenericArguments()[0], Visit(node.Arguments[0]));
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.IdOf))
            return RenderIdOf(Visit(node.Arguments[0]));
        return base.VisitMethodCall(node);
    }

    /// <summary>The source calls in a declaration, in order: each one's entity type, and whether it is a <c>Set</c>.</summary>
    public static IReadOnlyList<(Type Type, bool IsSet)> SourceCalls(LambdaExpression definition)
    {
        var finder = new SourceCallFinder();
        finder.Visit(definition.Body);
        return finder.Calls;
    }

    private sealed class SourceCallFinder : ExpressionVisitor
    {
        public List<(Type Type, bool IsSet)> Calls { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(ISparkViewSource) && node.Method.Name != nameof(ISparkViewSource.IdOf))
                Calls.Add((node.Method.GetGenericArguments()[0], node.Method.Name == nameof(ISparkViewSource.Set)));
            return base.VisitMethodCall(node);
        }
    }
}
