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

        // S16: a reduce groups the mapped rows, as RavenDB's map-reduce and SQL's GROUP BY do.
        if (view.ReduceDefinition is { } reduce)
            rows = reduce.Compile().DynamicInvoke(rows)!;

        // Materialized once: a query that is enumerated twice (count, then page) must not run the view twice. Its rows
        // compare strings ignoring case, as the collection's do (O20).
        var list = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(view.RowType).Invoke(null, [rows])!;
        return IgnoreCaseQuery.Wrap(Queryable.AsQueryable((System.Collections.IEnumerable)list));
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

        protected override Expression RenderIdOf(Expression row)
            => Expression.Call(Expression.Constant(session), typeof(ISparkSession).GetMethod(nameof(ISparkSession.GetId))!, Expression.Convert(row, typeof(object)));

        /// <summary>
        /// Every member read through something that can be null yields the default instead of throwing, as a database
        /// propagates NULL: a missing reference (<c>Load</c>), an absent complex value (<c>x.Address!.City</c>), a
        /// translation's dictionary.
        /// </summary>
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is null || node.Expression is ParameterExpression)
                return base.VisitMember(node);

            var inner = Visit(node.Expression);
            var read = Expression.MakeMemberAccess(inner, node.Member);
            return CanBeNull(inner.Type) ? NullSafe(inner, read, node.Type) : read;
        }

        /// <summary>
        /// A dictionary indexer reads a missing key as the default, as RavenDB reads a missing path. A fan-out over a
        /// missing list (<c>from item in x.List</c> with <c>List</c> null) yields no rows, as it does in RavenDB and SQL.
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == nameof(Queryable.SelectMany)
                && node.Arguments[1] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression items })
            {
                var visited = (LambdaExpression)Visit(items);
                var itemsType = visited.ReturnType;
                var empty = Expression.Constant(Array.CreateInstance(itemsType.GetGenericArguments()[0], 0), itemsType);
                var safe = Expression.Lambda(visited.Type,
                    Expression.Coalesce(Expression.Convert(visited.Body, itemsType), empty), visited.Parameters);
                return node.Update(null, [Visit(node.Arguments[0]), Expression.Quote(safe), .. node.Arguments.Skip(2).Select(Visit)!]);
            }

            if (node is { Method.Name: "get_Item", Object: { } target, Arguments.Count: 1 }
                && ReadOnlyDictionary(target.Type) is { } dictionary)
            {
                var visited = Visit(target);
                var lookup = Expression.Call(typeof(CollectionExtensions), nameof(CollectionExtensions.GetValueOrDefault),
                    dictionary.GetGenericArguments(), Expression.Convert(visited, dictionary), Visit(node.Arguments[0]));
                var read = lookup.Type == node.Type ? (Expression)lookup : Expression.Convert(lookup, node.Type);
                return NullSafe(visited, read, node.Type);
            }

            return base.VisitMethodCall(node);
        }

        private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

        private static Expression NullSafe(Expression inner, Expression read, Type type)
            => Expression.Condition(Expression.Equal(inner, Expression.Constant(null, inner.Type)), Expression.Default(type), read);

        private static Type? ReadOnlyDictionary(Type type)
            => type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        private static readonly MethodInfo QueryOfT = typeof(ISparkSession).GetMethods()
            .Single(m => m.Name == nameof(ISparkSession.Query) && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);

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
