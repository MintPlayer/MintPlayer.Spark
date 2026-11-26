using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// PRD §8.10, finding 5: EF loads every owned (AsDetail) collection with its owner, joined into the owner's statement, so
/// a type with two child tables reads the product of their rows (3 jobs × 4 courses = 12 rows per owner). A query rooted
/// at such a type runs split: the owner's rows, then one statement per child table. A type with one child table keeps
/// the single statement, which costs no extra round trip and returns one row per child.
/// </summary>
/// <remarks>
/// An interceptor, so every read gets it (session queries, loads, the change feed) without each call site saying
/// <c>AsSplitQuery()</c>. A query that states <c>AsSingleQuery()</c> keeps its one statement, as a view's does
/// (<see cref="RelationalViewRenderer"/>). The statements of a split read are not one snapshot: a write between them can
/// pair an owner with newer child rows, where a single statement (and a RavenDB document) is read whole.
/// </remarks>
internal static class SparkSplitQueries
{
    /// <summary>The one instance every <see cref="Model.SparkDbContext"/> adds: EF keeps a singleton interceptor per service provider.</summary>
    public static IInterceptor Interceptor { get; } = new SplitInterceptor();

    private static readonly MethodInfo AsSplitQuery = typeof(RelationalQueryableExtensions).GetMethod(nameof(RelationalQueryableExtensions.AsSplitQuery))!;
    private static readonly MethodInfo AsSingleQuery = typeof(RelationalQueryableExtensions).GetMethod(nameof(RelationalQueryableExtensions.AsSingleQuery))!;

    /// <summary>
    /// Whether a read of <paramref name="type"/> joins two or more child tables: its own (inherited ones included) and its
    /// derived types', whose rows a TPH query returns too. Child tables are the only owned types the model maps
    /// (<c>SparkDbContext.MapChildTable</c>), and a child row has no child table of its own, so there is nothing nested.
    /// </summary>
    internal static bool JoinsSeveralChildTables(IEntityType type)
        => type.GetNavigations()
            .Concat(type.GetDerivedTypes().SelectMany(t => t.GetDeclaredNavigations()))
            .Count(n => n.IsCollection && n.TargetEntityType.IsOwned()) >= 2;

    private sealed class SplitInterceptor : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            var finder = new SingleQueryFinder();
            finder.Visit(queryExpression);
            return finder.Found ? queryExpression : new Splitter().Visit(queryExpression);
        }
    }

    /// <summary>Finds an <c>AsSingleQuery()</c> the query states. (One that states <c>AsSplitQuery()</c> is split anyway.)</summary>
    private sealed class SingleQueryFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod && node.Method.GetGenericMethodDefinition() == AsSingleQuery)
            {
                Found = true;
                return node;
            }
            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// Wraps each root over a type with several child tables in <c>AsSplitQuery()</c>. EF takes the setting from anywhere
    /// in the query, and applies it to the whole of it.
    /// </summary>
    private sealed class Splitter : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
            => node is EntityQueryRootExpression root && JoinsSeveralChildTables(root.EntityType)
                ? Expression.Call(AsSplitQuery.MakeGenericMethod(root.ElementType), root)
                : base.VisitExtension(node);
    }
}
