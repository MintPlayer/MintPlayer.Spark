using MintPlayer.Spark.Abstractions.Reflection;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Expression plumbing for composing row filters (#460): rebinding a predicate written over one type
/// onto a parameter of another, and joining several predicates into one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never <see cref="Expression.Invoke(Expression, Expression[])"/>.</b> RavenDB's LINQ provider does
/// not translate an invocation of a lambda, so the only way two predicates reach the database as one
/// <c>where</c> is to share a single parameter. Rebinding substitutes the parameter; it does not wrap.
/// </para>
/// <para>
/// <b>By member name, re-resolved on the target type.</b> A policy written over an interface
/// (<c>ISoftDeletable x =&gt; x.IsDeleted != true</c>) is rebound onto the entity as
/// <c>e.IsDeleted</c> — a property access on the entity itself, not a cast to the interface. Spike S3
/// measured both shapes; the property shape is the one that is also meaningful on an index
/// projection (#285, D2), which implements no interface at all.
/// </para>
/// </remarks>
internal static class RowFilterExpressions
{
    /// <summary>
    /// Rebinds <paramref name="filter"/> (a one-parameter predicate) onto a fresh parameter of
    /// <paramref name="targetType"/>. Null when some use of the parameter cannot be expressed on the
    /// target type: a member it does not have, a member of a different type, or the parameter used
    /// as a whole value where the target is not assignable to the source.
    /// </summary>
    public static LambdaExpression? Rebind(LambdaExpression filter, Type targetType)
    {
        var parameter = Expression.Parameter(targetType, filter.Parameters[0].Name);
        var body = RebindBody(filter, parameter);
        return body is null ? null : Expression.Lambda(PredicateType(targetType), body, parameter);
    }

    /// <summary>The body of <paramref name="filter"/> with its parameter replaced by <paramref name="target"/>, or null.</summary>
    public static Expression? RebindBody(LambdaExpression filter, ParameterExpression target)
    {
        if (filter.Parameters.Count != 1)
            return null;

        var source = filter.Parameters[0];
        if (source.Type == target.Type)
            return source == target ? filter.Body : new ParameterSwap(source, target).Visit(filter.Body);

        var visitor = new MemberRebinder(source, target);
        var body = visitor.Visit(filter.Body);
        return visitor.Failed ? null : body;
    }

    /// <summary>
    /// Joins predicates over <paramref name="entityType"/> with <c>AndAlso</c>, constant-folded: a
    /// <c>false</c> anywhere makes the whole filter <c>false</c>, a <c>true</c> is dropped, and a null
    /// (no restriction for this caller) is skipped. Null when nothing restricts; a constant-bodied
    /// lambda when the answer does not depend on the row.
    /// </summary>
    /// <exception cref="InvalidOperationException">A filter cannot be rebound onto the entity type —
    /// a policy that claimed a type it cannot express a predicate over. Failing loudly is the only
    /// safe answer: dropping the predicate would widen the filter silently.</exception>
    public static LambdaExpression? Combine(Type entityType, IEnumerable<(LambdaExpression Filter, string Source)> filters)
    {
        var parameter = Expression.Parameter(entityType, "row");
        Expression? combined = null;

        foreach (var (filter, source) in filters)
        {
            var body = RebindBody(filter, parameter)
                ?? throw new InvalidOperationException(
                    $"Row filter from {source} is written over '{filter.Parameters[0].Type.Name}' and cannot be " +
                    $"rebound onto '{entityType.Name}' by member name. Every member it reads must exist on " +
                    $"'{entityType.Name}' with the same type.");

            if (body is ConstantExpression { Value: bool constant })
            {
                if (!constant)
                    return Expression.Lambda(PredicateType(entityType), Expression.Constant(false), parameter);
                continue;
            }

            combined = combined is null ? body : Expression.AndAlso(combined, body);
        }

        return combined is null ? null : Expression.Lambda(PredicateType(entityType), combined, parameter);
    }

    /// <summary><c>Func&lt;T, bool&gt;</c>, cached per type.</summary>
    public static Type PredicateType(Type type)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type>(
            ("RowFilterExpressions.PredicateType", type),
            static k => typeof(Func<,>).MakeGenericType(k.Type, typeof(bool)));

    private sealed class ParameterSwap(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : node;
    }

    private sealed class MemberRebinder(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        public bool Failed { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (Unwrap(node.Expression) is not { } owner || owner != source)
                return base.VisitMember(node);

            var property = target.Type.GetCachedProperty(node.Member.Name);
            if (property is { CanRead: true } && property.PropertyType == node.Type)
                return Expression.Property(target, property);

            var field = property is null
                ? target.Type.GetField(node.Member.Name, BindingFlags.Public | BindingFlags.Instance)
                : null;
            if (field is not null && field.FieldType == node.Type)
                return Expression.Field(target, field);

            Failed = true;
            return node;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node != source)
                return node;

            // The parameter as a whole value (passed to a method, compared by reference). Expressible
            // only when the target IS a source — and then only as a cast, which a database provider
            // may not translate; the member path above is the one policies should take.
            if (source.Type.IsAssignableFrom(target.Type))
                return Expression.Convert(target, source.Type);

            Failed = true;
            return node;
        }

        /// <summary>The expression a member is read from, looking through the casts a compiler
        /// inserts for an interface- or base-typed access (<c>((ISoftDeletable)x).IsDeleted</c>).</summary>
        private static Expression? Unwrap(Expression? expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } unary)
                expression = unary.Operand;
            return expression;
        }
    }
}
