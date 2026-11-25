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
/// <item>The shape is <c>Set&lt;T&gt;().Select(…)</c>: RavenDB's map takes one source collection.</item>
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

    /// <summary>Whether <paramref name="node"/> is a call on the view's source.</summary>
    protected bool IsSourceCall(Expression? node, string method)
        => node is MethodCallExpression call && call.Object == source && call.Method.Name == method;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Set))
            return RenderSet(node.Method.GetGenericArguments()[0]);
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Load))
            return RenderLoad(node.Method.GetGenericArguments()[0], Visit(node.Arguments[0]));
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
            if (node.Method.DeclaringType == typeof(ISparkViewSource))
                Calls.Add((node.Method.GetGenericArguments()[0], node.Method.Name == nameof(ISparkViewSource.Set)));
            return base.VisitMethodCall(node);
        }
    }
}
