using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// The query half of D26: <c>x.Skills</c>, a reference list EF ignores, becomes a subquery over its join table. Covers
/// the shapes the query pipeline emits for a collection column: <c>x.L != null</c>, <c>Any</c>, <c>Contains</c> and
/// <c>Count</c> (S2b).
/// </summary>
internal sealed class JoinRewriter : ExpressionVisitor
{
    private readonly SparkDbContext db;

    private JoinRewriter(SparkDbContext db) => this.db = db;

    public static IQueryable<T> Rewrite<T>(SparkDbContext db, IQueryable<T> query)
    {
        if (db.SparkModel.Joins.Count == 0)
            return query;
        var rewritten = new JoinRewriter(db).Visit(query.Expression);
        return ReferenceEquals(rewritten, query.Expression) ? query : query.Provider.CreateQuery<T>(rewritten);
    }

    private (SparkJoinTable Join, Expression Owner)? AsJoin(Expression? e)
        => e is MemberExpression { Member: PropertyInfo p, Expression: { } owner }
           && db.SparkModel.Joins.FirstOrDefault(j => j.Property.Name == p.Name && j.Owner.IsAssignableFrom(owner.Type)) is { } join
            ? (join, owner)
            : null;

    private Expression Subquery(SparkJoinTable join, Expression owner)
    {
        var ownerTable = db.SparkModel.RequireTable(join.Owner);
        var root = db.Set<Dictionary<string, object>>(join.Name).AsQueryable().Expression;
        var r = Expression.Parameter(typeof(Dictionary<string, object>), "r");
        var ownerId = Expression.Property(Expression.Convert(owner, ownerTable.Root), ownerTable.IdProperty);
        var rowOwner = Expression.Call(typeof(EF), nameof(EF.Property), [ownerTable.IdProperty.PropertyType], r, Expression.Constant(SparkDbContext.OwnerIdProperty));
        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [r.Type], root,
            Expression.Quote(Expression.Lambda(Expression.Equal(rowOwner, ownerId), r)));
        var target = Expression.Call(typeof(EF), nameof(EF.Property), [join.ElementType], r, Expression.Constant(SparkDbContext.TargetIdProperty));
        return Expression.Call(typeof(Queryable), nameof(Queryable.Select), [r.Type, join.ElementType], where,
            Expression.Quote(Expression.Lambda(target, r)));
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        // A reference list always exists (possibly empty): x.L != null is true, x.L == null is false.
        if (node.NodeType is ExpressionType.NotEqual or ExpressionType.Equal
            && ((AsJoin(node.Left) is not null && node.Right is ConstantExpression { Value: null })
                || (AsJoin(node.Right) is not null && node.Left is ConstantExpression { Value: null })))
            return Expression.Constant(node.NodeType == ExpressionType.NotEqual);
        return base.VisitBinary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.DeclaringType == typeof(Enumerable) && node.Arguments.Count > 0 && AsJoin(node.Arguments[0]) is var (join, owner)
            && node.Method.Name is nameof(Enumerable.Any) or nameof(Enumerable.Contains) or nameof(Enumerable.Count))
        {
            var sub = Subquery(join, Visit(owner));
            var rest = node.Arguments.Skip(1).Select(a => a is LambdaExpression l ? (Expression)Expression.Quote((LambdaExpression)Visit(l)) : Visit(a));
            return Expression.Call(typeof(Queryable), node.Method.Name, [join.ElementType], [sub, .. rest]);
        }

        if (node.Method.Name == nameof(List<string>.Contains) && node.Object is not null && AsJoin(node.Object) is var (j2, o2))
            return Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [j2.ElementType], Subquery(j2, Visit(o2)), Visit(node.Arguments[0]));

        return base.VisitMethodCall(node);
    }
}
