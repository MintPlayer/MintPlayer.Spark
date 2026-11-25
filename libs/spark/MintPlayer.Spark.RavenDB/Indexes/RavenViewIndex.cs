using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.Spark.Abstractions.Views;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Indexes;

/// <summary>
/// A <see cref="SparkView{TRow}"/> as a RavenDB static index (#536 D32, S5): <c>Set&lt;T&gt;().Select(…)</c> becomes
/// <c>docs =&gt; docs.Select(…)</c>, <c>Load&lt;T&gt;(id)</c> becomes <c>LoadDocument</c>, and every field is stored so
/// <c>ProjectInto</c> reads the computed values. Deployed under the view's name, unless a native index of that name
/// shadows it (the generator emits both for a RavenDB app).
/// </summary>
internal static class RavenViewIndexes
{
    public static IAbstractIndexCreationTask Render(ISparkView view)
        => (IAbstractIndexCreationTask)Activator.CreateInstance(typeof(RavenViewIndex<>).MakeGenericType(view.CollectionType), view)!;

    public static IAbstractIndexCreationTask Render(Type viewType) => Render(SparkViewEntries.Instantiate(viewType));
}

internal sealed class RavenViewIndex<TEntity> : SparkIndexCreationTask<TEntity>
{
    private readonly string name;

    public RavenViewIndex(ISparkView view)
    {
        name = view.Name;
        var definition = view.Definition;
        if (definition.Body is not MethodCallExpression { Method.Name: nameof(Queryable.Select) } select
            || select.Arguments[0] is not MethodCallExpression { Method.Name: nameof(ISparkViewSource.Set) } set
            || set.Object != definition.Parameters[0])
            throw new NotSupportedException(
                $"View {view.Name} cannot be a RavenDB index: its declaration must have the shape Set<T>().Select(…), with one source collection.");

        var projection = (LambdaExpression)StripQuotes(select.Arguments[1]);
        var rewritten = (LambdaExpression)new Rewriter(definition, this).Visit(projection);
        var docs = Expression.Parameter(typeof(IEnumerable<TEntity>), "docs");
        var map = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), [typeof(TEntity), view.RowType], docs, rewritten);
        Map = Expression.Lambda<Func<IEnumerable<TEntity>, System.Collections.IEnumerable>>(map, docs);
        StoreAllFields(FieldStorage.Yes);
    }

    public override string IndexName => name;

    private static Expression StripQuotes(Expression e) => e is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : e;

    private sealed class Rewriter(LambdaExpression definition, RavenViewIndex<TEntity> index) : SparkViewRewriter(definition)
    {
        private static readonly MethodInfo LoadDocument = typeof(AbstractCommonApiForIndexes)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "LoadDocument" && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string));

        protected override Expression RenderSet(Type type)
            => throw new NotSupportedException("A RavenDB map reads one collection: Set<T>() can appear only at the start of a view.");

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
