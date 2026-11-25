using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// A <see cref="SparkView{TRow}"/> as LINQ to objects over the session's rows (#536 D32, S5): <c>Set&lt;T&gt;()</c> is
/// the collection, <c>Load&lt;T&gt;(id)</c> a lookup by id. Member access through <c>Load</c> is made null-safe, so a
/// missing reference reads as <see langword="null"/> (or the default), as it does in SQL and RavenDB.
/// </summary>
internal static class InMemoryViewRenderer
{
    public static IQueryable Render(ISparkView view, ISparkSession session)
    {
        var body = new Rewriter(view.Definition, session).Visit(view.Definition.Body);
        var rows = Expression.Lambda(body).Compile().DynamicInvoke()!;

        // Materialized once: a query that is enumerated twice (count, then page) must not run the view twice.
        var list = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(view.RowType).Invoke(null, [rows])!;
        return Queryable.AsQueryable((System.Collections.IEnumerable)list);
    }

    private sealed class Rewriter(LambdaExpression definition, ISparkSession session) : SparkViewRewriter(definition)
    {
        private readonly Dictionary<Type, object> lookups = [];

        protected override Expression RenderSet(Type type)
            => Expression.Constant(Query(type), typeof(IQueryable<>).MakeGenericType(type));

        protected override Expression RenderLoad(Type type, Expression id)
        {
            var key = id.Type == typeof(string) ? id : Expression.Call(typeof(Rewriter), nameof(IdText), [], Expression.Convert(id, typeof(object)));
            var lookup = Lookup(type);
            return Expression.Call(typeof(Rewriter), nameof(Find), [type], Expression.Constant(lookup), key);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (!IsSourceCall(node.Expression, nameof(ISparkViewSource.Load)))
                return base.VisitMember(node);

            var loaded = Visit(node.Expression!);
            var read = Expression.MakeMemberAccess(loaded, node.Member);
            return Expression.Condition(
                Expression.Equal(loaded, Expression.Constant(null, loaded.Type)),
                Expression.Default(node.Type),
                read);
        }

        private static readonly MethodInfo QueryOfT = typeof(ISparkSession).GetMethods()
            .Single(m => m.Name == nameof(ISparkSession.Query) && m.GetGenericArguments().Length == 1);

        private object Query(Type type) => QueryOfT.MakeGenericMethod(type).Invoke(session, [])!;

        /// <summary>The rows of <paramref name="type"/> by id (case-insensitive, as ids are), built once per render.</summary>
        private object Lookup(Type type)
        {
            if (lookups.TryGetValue(type, out var lookup))
                return lookup;

            var byId = (System.Collections.IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), type), StringComparer.OrdinalIgnoreCase)!;
            foreach (var row in (System.Collections.IEnumerable)Query(type))
                if (session.GetId(row) is { } rowId)
                    byId[rowId] = row;
            return lookups[type] = byId;
        }

        private static T? Find<T>(Dictionary<string, T> rows, string? id) where T : class
            => id is null ? null : rows.GetValueOrDefault(id);

        /// <summary>An <c>Id&lt;T&gt;</c> (or its nullable) as its text; <see langword="null"/> for an empty one.</summary>
        private static string? IdText(object? id) => id switch
        {
            null => null,
            Abstractions.ISparkId sparkId => sparkId.IsEmpty ? null : sparkId.Value,
            _ => id.ToString(),
        };
    }
}
