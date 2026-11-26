using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Converters;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// Rewrites LINQ written against an entity's typed <c>x.Id</c> into what RavenDB's client translates, at execution
/// time (#536 D23 rule 7 (b), spike S-ID1 R1–R7):
/// <list type="bullet">
/// <item><c>x.Id == v</c> and <c>!=</c> become a comparison of the generated identity member, so the query says
/// <c>id()</c>, which matches the document's key ignoring case and finds documents with no <c>Id</c> in their
/// body (every document)</item>
/// <item><c>OrderBy(x =&gt; x.Id)</c> (and the other orderings) order by the identity member</item>
/// <item><c>coll.Contains(x.Member)</c>, for any element type, becomes <c>x.Member.In(coll)</c>; RavenDB's client
/// rejects <c>Contains</c> even over strings (S-ID1 C4)</item>
/// </list>
/// A reference compared with an <see cref="Abstractions.Id{T}"/> value needs no rewrite: the store's query value
/// converter sends its canonical string.
/// </summary>
public static class RavenIdQueryRewriter
{
    private static readonly MethodInfo InMethod = typeof(RavenQueryableExtensions).GetMethods()
        .Single(static m => m.Name == nameof(RavenQueryableExtensions.In)
            && m.GetParameters()[1].ParameterType.IsGenericType
            && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    /// <summary><paramref name="query"/> with its expression rewritten, or itself when nothing needed it.</summary>
    public static IQueryable<T> Rewrite<T>(IQueryable<T> query)
    {
        var rewritten = new Visitor().Visit(query.Expression);
        return ReferenceEquals(rewritten, query.Expression) ? query : query.Provider.CreateQuery<T>(rewritten);
    }

    /// <summary>The untyped <see cref="Rewrite{T}"/>.</summary>
    public static IQueryable Rewrite(IQueryable query)
    {
        var rewritten = new Visitor().Visit(query.Expression);
        return ReferenceEquals(rewritten, query.Expression) ? query : query.Provider.CreateQuery(rewritten);
    }

    private sealed class Visitor : ExpressionVisitor
    {
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
            {
                if (OwnIdentity(node.Left) is { } left && !UsesParameter(node.Right))
                    return Expression.MakeBinary(node.NodeType, left, Expression.Constant(Text(node.Right), typeof(string)));
                if (OwnIdentity(node.Right) is { } right && !UsesParameter(node.Left))
                    return Expression.MakeBinary(node.NodeType, Expression.Constant(Text(node.Left), typeof(string)), right);
            }
            return base.VisitBinary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Orderings by the entity's own id: rebuild the call with the identity member as a string key.
            if (node.Method.DeclaringType == typeof(Queryable)
                && node.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)
                && node.Arguments.Count == 2
                && StripQuote(node.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } key
                && OwnIdentity(key.Body) is { } identity)
            {
                var source = Visit(node.Arguments[0]);
                var lambda = Expression.Lambda(identity, key.Parameters);
                var method = node.Method.GetGenericMethodDefinition().MakeGenericMethod(key.Parameters[0].Type, typeof(string));
                return Expression.Call(method, source, Expression.Quote(lambda));
            }

            Expression? collection = null, item = null;
            // C# 14 may bind MemoryExtensions.Contains(span, item, comparer: null): the null comparer is the default.
            if (node.Method.Name == "Contains" && node.Method.IsStatic
                && (node.Arguments.Count == 2 || node.Arguments is [_, _, ConstantExpression { Value: null }]))
                (collection, item) = (node.Arguments[0], node.Arguments[1]);
            else if (node.Method.Name == "Contains" && !node.Method.IsStatic && node.Arguments.Count == 1 && node.Object is not null && node.Object.Type != typeof(string))
                (collection, item) = (node.Object, node.Arguments[0]);

            // C# 14 binds array.Contains(x) to MemoryExtensions.Contains(ReadOnlySpan<T>, T): unwrap the span.
            if (collection is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } span)
                collection = span.Arguments[0];
            while (collection is UnaryExpression { NodeType: ExpressionType.Convert } convert && !UsesParameter(convert))
                collection = convert.Operand;

            if (collection is null || item is null || !UsesParameter(item) || UsesParameter(collection))
                return base.VisitMethodCall(node);

            if (OwnIdentity(item) is { } own)
            {
                var texts = SparkIdValues.ToTexts(Evaluate(collection)).ToArray();
                return Expression.Call(InMethod.MakeGenericMethod(typeof(string)), own, Expression.Constant(texts, typeof(IEnumerable<string>)));
            }

            var enumerable = typeof(IEnumerable<>).MakeGenericType(item.Type);
            if (!enumerable.IsAssignableFrom(collection.Type))
                return base.VisitMethodCall(node);
            return Expression.Call(InMethod.MakeGenericMethod(item.Type), Visit(item), Expression.Convert(collection, enumerable));
        }

        /// <summary>The identity member for an entity's own <c>x.Id</c> of type <c>Id&lt;Self&gt;</c>, else null.</summary>
        private static Expression? OwnIdentity(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                expression = convert.Operand;
            if (expression is not MemberExpression { Member.Name: "Id", Expression: { } owner } member
                || SparkIdValues.TargetOf(member.Type) is not { } target
                || !target.IsAssignableFrom(owner.Type)
                || SparkDocumentIdentity.Of(owner.Type) is not { } identity)
                return null;
            return Expression.Property(owner, identity);
        }

        private static Expression StripQuote(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

        private static bool UsesParameter(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder.Found;
        }

        private static object? Evaluate(Expression expression)
            => expression is ConstantExpression constant
                ? constant.Value
                : Expression.Lambda(expression).Compile().DynamicInvoke();

        private static string? Text(Expression expression) => SparkIdValues.ToText(Evaluate(expression));
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }
}
