using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Converters;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark;

/// <summary>
/// Rewrites an index map written against typed ids into what RavenDB's index translator turns into working server-side
/// code (#536 D23, spike S-ID6):
/// <list type="bullet">
/// <item>the entity's own <c>x.Id</c>, <c>x.Id.Value</c> and <c>x.Id.ToString()</c> become the generated identity
/// member, which the translator writes as <c>Id(x)</c>. The own id is not in the document body, so the member
/// access the translator would otherwise write reads null.</item>
/// <item>a reference's <c>r.Value</c> (and <c>r!.Value.Value</c> on a nullable one) becomes <c>r.ToString()</c>. The
/// server sees the stored string, which has no <c>Value</c> member, so the index would fail on every document.</item>
/// </list>
/// </summary>
public static class RavenIndexIdRewriter
{
    private static readonly MethodInfo ObjectToString = typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;

    /// <summary><paramref name="expression"/> rewritten, or itself when nothing needed it.</summary>
    public static TExpression Rewrite<TExpression>(TExpression expression) where TExpression : Expression
        => (TExpression)new Visitor().Visit(expression);

    private sealed class Visitor : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            // x.Id.Value / r.Value: the id's text.
            if (node.Member.Name == "Value" && node.Expression is { } inner && SparkIdValues.TargetOf(inner.Type) is not null && node.Type == typeof(string))
                return Text(inner);

            // The own x.Id used as an Id<T>: keep its type, read it from the identity.
            if (OwnIdentity(node) is { } identity)
                return Expression.Convert(identity, node.Type, FromTrusted(node.Type));

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(ToString) && node.Arguments.Count == 0 && node.Object is { } target && SparkIdValues.TargetOf(target.Type) is not null)
                return Text(target);
            return base.VisitMethodCall(node);
        }

        private Expression Text(Expression id)
        {
            // r!.Value on a Nullable<Id<T>> is the Id<T> itself for this purpose.
            if (id is MemberExpression { Member.Name: "Value", Expression: { } nullable } && Nullable.GetUnderlyingType(nullable.Type) is not null)
                id = nullable;
            while (id is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                id = convert.Operand;

            if (OwnIdentity(id) is { } identity)
                return identity;
            return Expression.Call(Visit(id), ObjectToString);
        }

        private Expression? OwnIdentity(Expression expression)
        {
            if (expression is not MemberExpression { Member.Name: "Id", Expression: { } owner } member
                || SparkIdValues.TargetOf(member.Type) is not { } target
                || Nullable.GetUnderlyingType(member.Type) is not null
                || !target.IsAssignableFrom(owner.Type)
                || SparkDocumentIdentity.Of(owner.Type) is not { } identity)
                return null;
            return Expression.Property(Visit(owner), identity);
        }

        private static MethodInfo FromTrusted(Type idType)
            => idType.GetMethod("FromTrusted", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
    }
}
