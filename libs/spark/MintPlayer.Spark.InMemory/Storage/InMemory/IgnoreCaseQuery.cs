using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// #536 O20: a query over the in-memory store compares strings ignoring case, as RavenDB's auto-indexes (which lowercase
/// their terms) and the SQL stores' collations do. LINQ to objects compares ordinally, so the query's expression is
/// rewritten before it runs: <c>==</c>, <c>!=</c> and <c>Equals</c> between strings, a string's <c>StartsWith</c>,
/// <c>EndsWith</c> and <c>Contains</c>, and <c>Contains</c> on a list or array of strings take
/// <see cref="StringComparison.OrdinalIgnoreCase"/>. A call that states its own comparison, and a set's <c>Contains</c>
/// (the set carries its comparer), are left as written.
/// </summary>
internal static class IgnoreCaseQuery
{
    /// <summary><paramref name="source"/> (LINQ to objects), whose queries compare strings ignoring case.</summary>
    public static IQueryable<T> Wrap<T>(IQueryable<T> source) => new Query<T>(new Provider(source.Provider), source.Expression);

    /// <inheritdoc cref="Wrap{T}(IQueryable{T})"/>
    public static IQueryable Wrap(IQueryable source)
        => (IQueryable)Activator.CreateInstance(typeof(Query<>).MakeGenericType(source.ElementType), new Provider(source.Provider), source.Expression)!;

    private sealed class Provider : IQueryProvider
    {
        private readonly IQueryProvider inner;

        public Provider(IQueryProvider inner) => this.inner = inner;

        public IQueryable CreateQuery(Expression expression)
            => (IQueryable)Activator.CreateInstance(typeof(Query<>).MakeGenericType(ElementType(expression.Type)), this, expression)!;

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new Query<TElement>(this, expression);

        public object? Execute(Expression expression) => inner.Execute(Rewriter.Instance.Visit(expression));

        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(Rewriter.Instance.Visit(expression));

        public IEnumerator<T> Enumerate<T>(Expression expression) => inner.CreateQuery<T>(Rewriter.Instance.Visit(expression)).GetEnumerator();

        private static Type ElementType(Type sequence)
            => (sequence.IsGenericType && sequence.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    ? sequence
                    : sequence.GetInterfaces().First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                .GetGenericArguments()[0];
    }

    private sealed class Query<T> : IOrderedQueryable<T>
    {
        private readonly Provider provider;

        public Query(Provider provider, Expression expression)
        {
            this.provider = provider;
            Expression = expression;
        }

        public Type ElementType => typeof(T);

        public Expression Expression { get; }

        public IQueryProvider Provider => provider;

        public IEnumerator<T> GetEnumerator() => provider.Enumerate<T>(Expression);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Rewriter : ExpressionVisitor
    {
        public static readonly Rewriter Instance = new();

        private static readonly ConstantExpression IgnoreCase = Expression.Constant(StringComparison.OrdinalIgnoreCase);
        private static readonly ConstantExpression Comparer = Expression.Constant(StringComparer.OrdinalIgnoreCase, typeof(IEqualityComparer<string>));
        private static readonly MethodInfo StringEquals = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;
        private static readonly MethodInfo SequenceContains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 3)
            .MakeGenericMethod(typeof(string));

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && node.Left.Type == typeof(string) && node.Right.Type == typeof(string))
            {
                var equals = Expression.Call(StringEquals, Visit(node.Left), Visit(node.Right), IgnoreCase);
                return node.NodeType == ExpressionType.Equal ? equals : Expression.Not(equals);
            }
            return base.VisitBinary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            var parameters = method.GetParameters();

            if (method.DeclaringType == typeof(string) && parameters.All(p => p.ParameterType == typeof(string)))
            {
                switch (method.Name, method.IsStatic, parameters.Length)
                {
                    case (nameof(string.Equals), true, 2):
                        return Expression.Call(StringEquals, Visit(node.Arguments[0]), Visit(node.Arguments[1]), IgnoreCase);
                    case (nameof(string.Equals), false, 1):
                        return Expression.Call(StringEquals, Visit(node.Object!), Visit(node.Arguments[0]), IgnoreCase);
                    case (nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains), false, 1):
                        return Expression.Call(Visit(node.Object!),
                            typeof(string).GetMethod(method.Name, [typeof(string), typeof(StringComparison)])!, Visit(node.Arguments[0]), IgnoreCase);
                }
            }

            // Enumerable.Contains(strings, value), and C# 14's MemoryExtensions.Contains(span, value) over an array.
            if (method is { IsStatic: true, Name: nameof(Enumerable.Contains), IsGenericMethod: true } && parameters.Length == 2
                && method.GetGenericArguments()[0] == typeof(string)
                && (method.DeclaringType == typeof(Enumerable) || method.DeclaringType == typeof(MemoryExtensions))
                && Sequence(node.Arguments[0]) is { } sequence)
                return Expression.Call(SequenceContains, Visit(sequence), Visit(node.Arguments[1]), Comparer);

            // A list's own Contains (List<string>.Contains); a set's is left to the set's comparer.
            if (method is { IsStatic: false, Name: "Contains" } && parameters.Length == 1 && parameters[0].ParameterType == typeof(string)
                && typeof(IEnumerable<string>).IsAssignableFrom(method.DeclaringType)
                && !typeof(ISet<string>).IsAssignableFrom(method.DeclaringType) && !typeof(IReadOnlySet<string>).IsAssignableFrom(method.DeclaringType))
                return Expression.Call(SequenceContains, Visit(node.Object!), Visit(node.Arguments[0]), Comparer);

            return base.VisitMethodCall(node);
        }

        /// <summary>The sequence of strings behind <paramref name="argument"/>, unwrapping C# 14's implicit span conversion.</summary>
        private static Expression? Sequence(Expression argument)
        {
            if (typeof(IEnumerable<string>).IsAssignableFrom(argument.Type))
                return argument;
            return argument switch
            {
                MethodCallExpression { Method.Name: "op_Implicit" or nameof(MemoryExtensions.AsSpan), Arguments: [var array] }
                    when typeof(IEnumerable<string>).IsAssignableFrom(array.Type) => array,
                UnaryExpression { NodeType: ExpressionType.Convert, Operand: var array } when typeof(IEnumerable<string>).IsAssignableFrom(array.Type) => array,
                _ => null,
            };
        }
    }
}
