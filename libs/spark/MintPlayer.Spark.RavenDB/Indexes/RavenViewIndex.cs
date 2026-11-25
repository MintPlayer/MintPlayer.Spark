using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.Spark.Abstractions.Views;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Indexes;

/// <summary>
/// A <see cref="SparkView{TRow}"/> as a RavenDB static index (#536 D32, S5): <c>Set&lt;T&gt;().Where(…).Select(…)</c>
/// becomes <c>docs =&gt; docs.Where(…).Select(…)</c>, <c>Load&lt;T&gt;(id)</c> becomes <c>LoadDocument</c>, and every
/// field is stored so <c>ProjectInto</c> reads the computed values. Maps joined by <c>Concat</c> are a multi-map index
/// (one <c>AddMap</c> per collection), and a view with a reduce is a map-reduce index (S16). Deployed under the view's
/// name, unless a native index of that name shadows it (the generator emits both for a RavenDB app).
/// </summary>
internal static class RavenViewIndexes
{
    public static IAbstractIndexCreationTask Render(ISparkView view)
        => view.ReduceDefinition is null && Branches(view).Count == 1
            ? (IAbstractIndexCreationTask)Activator.CreateInstance(typeof(RavenViewIndex<>).MakeGenericType(view.CollectionType), view)!
            : (IAbstractIndexCreationTask)Activator.CreateInstance(typeof(RavenMultiMapViewIndex<>).MakeGenericType(view.RowType), view)!;

    public static IAbstractIndexCreationTask Render(Type viewType) => Render(SparkViewEntries.Instantiate(viewType));

    /// <summary>The view's maps: the operands of its <c>Concat</c>s, in order, or the body alone.</summary>
    internal static IReadOnlyList<Expression> Branches(ISparkView view)
    {
        var branches = new List<Expression>();
        Collect(view.Definition.Body);
        return branches;

        void Collect(Expression e)
        {
            if (e is MethodCallExpression { Method.Name: nameof(Queryable.Concat), Arguments: [var left, var right] } concat
                && concat.Method.DeclaringType == typeof(Queryable))
            {
                Collect(left);
                Collect(right);
            }
            else
                branches.Add(e);
        }
    }

    /// <summary>
    /// One map, <c>Set&lt;T&gt;().Where(…).Select(…)</c>, as RavenDB's <c>docs =&gt; …</c> over <c>T</c>, which
    /// <paramref name="entityType"/> returns; <paramref name="index"/> owns <c>LoadDocument</c>. A fan-out map
    /// (<c>from x in Set&lt;T&gt;() from item in x.List select …</c>, a <c>SelectMany</c> with a result selector) is
    /// RavenDB's own fan-out: an index entry per item.
    /// </summary>
    internal static LambdaExpression Map(ISparkView view, Expression branch, AbstractCommonApiForIndexes index, out Type entityType)
    {
        var definition = view.Definition;
        if (branch is not MethodCallExpression { Method.Name: nameof(Queryable.Select) or nameof(Queryable.SelectMany) } select
            || select.Method.DeclaringType != typeof(Queryable)
            || select.Arguments.Count != (select.Method.Name == nameof(Queryable.Select) ? 2 : 3))
            throw Shape(view);

        var filters = new Stack<LambdaExpression>();
        var source = select.Arguments[0];
        while (source is MethodCallExpression { Method.Name: nameof(Queryable.Where), Arguments: [var inner, var predicate] } where
               && where.Method.DeclaringType == typeof(Queryable))
        {
            filters.Push((LambdaExpression)StripQuotes(predicate));
            source = inner;
        }
        if (source is not MethodCallExpression { Method.Name: nameof(ISparkViewSource.Set) } set || set.Object != definition.Parameters[0])
            throw Shape(view);

        entityType = set.Method.GetGenericArguments()[0];
        var rewriter = new Rewriter(definition, index);
        var docs = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(entityType), "docs");
        Expression chain = docs;
        foreach (var filter in filters)
            chain = Expression.Call(typeof(Enumerable), nameof(Enumerable.Where), [entityType], chain, rewriter.Visit(filter));

        Expression map;
        if (select.Method.Name == nameof(Queryable.SelectMany))
        {
            var items = (LambdaExpression)rewriter.Visit(StripQuotes(select.Arguments[1]));
            var result = (LambdaExpression)rewriter.Visit(StripQuotes(select.Arguments[2]));
            map = Expression.Call(typeof(Enumerable), nameof(Enumerable.SelectMany),
                [entityType, result.Parameters[1].Type, view.RowType], chain, items, result);
        }
        else
        {
            var projection = (LambdaExpression)rewriter.Visit(StripQuotes(select.Arguments[1]));
            map = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), [entityType, view.RowType], chain, projection);
        }
        return Expression.Lambda(typeof(Func<,>).MakeGenericType(docs.Type, typeof(System.Collections.IEnumerable)), map, docs);
    }

    private static NotSupportedException Shape(ISparkView view) => new(
        $"View {view.Name} cannot be a RavenDB index: each map must have the shape Set<T>().Where(…).Select(…), or "
        + "from x in Set<T>() from item in x.List select … for a fan-out, maps joined by Concat.");

    private static Expression StripQuotes(Expression e) => e is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : e;

    private sealed class Rewriter(LambdaExpression definition, AbstractCommonApiForIndexes index) : SparkViewRewriter(definition)
    {
        private static readonly MethodInfo LoadDocument = typeof(AbstractCommonApiForIndexes)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "LoadDocument" && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string));

        protected override Expression RenderSet(Type type)
            => throw new NotSupportedException("A RavenDB map reads one collection: Set<T>() can appear only at the start of a map.");

        /// <remarks>A typed id is passed as its text, which RavenDB evaluates against the stored string (S-ID6).</remarks>
        protected override Expression RenderLoad(Type type, Expression id)
        {
            var text = id.Type == typeof(string) ? id : Expression.Call(id, id.Type.GetMethod(nameof(ToString), Type.EmptyTypes)!);
            return Expression.Call(Expression.Constant(index), LoadDocument.MakeGenericMethod(type), text);
        }

        /// <summary>RavenDB gives a projection its document id, so the row's <c>Id</c> binding is dropped (S5 finding 9).</summary>
        protected override Expression VisitMemberInit(MemberInitExpression node)
            => Expression.MemberInit(node.NewExpression, node.Bindings
                .Where(b => b.Member.Name != "Id")
                .Select(b => b is MemberAssignment a ? Expression.Bind(a.Member, Visit(a.Expression)) : b));
    }
}

internal sealed class RavenViewIndex<TEntity> : SparkIndexCreationTask<TEntity>
{
    private readonly string name;

    public RavenViewIndex(ISparkView view)
    {
        name = view.Name;
        Map = (Expression<Func<IEnumerable<TEntity>, System.Collections.IEnumerable>>)RavenViewIndexes.Map(view, view.Definition.Body, this, out _);
        StoreAllFields(FieldStorage.Yes);
    }

    public override string IndexName => name;
}

/// <summary>
/// A view over several collections (<c>Concat</c>) or with a reduce (#536 S16): RavenDB's multi-map index, one
/// <c>AddMap</c> per map, whose reduce output is the maps' row type.
/// </summary>
internal sealed class RavenMultiMapViewIndex<TRow> : AbstractMultiMapIndexCreationTask<TRow>
{
    private static readonly MethodInfo AddMapOfT = typeof(AbstractMultiMapIndexCreationTask<TRow>)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(m => m.Name == "AddMap" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

    private readonly string name;

    public RavenMultiMapViewIndex(ISparkView view)
    {
        name = view.Name;
        foreach (var branch in RavenViewIndexes.Branches(view))
        {
            var map = RavenViewIndexes.Map(view, branch, this, out var entityType);
            AddMapOfT.MakeGenericMethod(entityType).Invoke(this, [map]);
        }

        if (view.ReduceDefinition is { } reduce)
            Reduce = Expression.Lambda<Func<IEnumerable<TRow>, System.Collections.IEnumerable>>(reduce.Body, reduce.Parameters);
        else
            StoreAllFields(FieldStorage.Yes);
    }

    public override string IndexName => name;
}
