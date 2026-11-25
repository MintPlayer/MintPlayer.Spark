using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The executor composes filter values as captured fields, never as <see cref="Expression.Constant(object)"/> (#536 S2b,
/// PRD §8.10 finding 3). EF writes a constant into the SQL as a literal, one query plan per value; a field read off a
/// constant holder is what a C# closure compiles to, and EF turns it into a parameter. RavenDB and LINQ to objects
/// evaluate both the same way.
/// </summary>
public class CapturedFilterValueTests
{
    private sealed class Row
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public string[]? Tags { get; set; }
    }

    private static MemberExpression Member(string name) => Expression.Property(Expression.Parameter(typeof(Row), "x"), name);

    private static IReadOnlyList<ConstantExpression> ValueConstants(Expression expression)
    {
        var found = new List<ConstantExpression>();
        new Collector(found).Visit(expression);
        // A null constant is the IS NULL guard, and the holder a captured field hangs off is not a value either.
        return [.. found.Where(c => c.Value is not null && !c.Type.Name.StartsWith("Captured", StringComparison.Ordinal))];
    }

    private sealed class Collector(List<ConstantExpression> found) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
        {
            found.Add(node);
            return node;
        }
    }

    [Fact]
    public void A_scalar_filter_value_is_captured()
    {
        var predicate = QueryExecutor.AnyEquals(Member(nameof(Row.Name)), ["alice", "bob"], typeof(string))!;

        ValueConstants(predicate).Should().BeEmpty();
    }

    [Fact]
    public void A_value_type_filter_value_is_captured()
    {
        var predicate = QueryExecutor.AnyEquals(Member(nameof(Row.Age)), [42], typeof(int))!;

        ValueConstants(predicate).Should().BeEmpty();
    }

    [Fact]
    public void A_collection_filter_value_is_captured()
    {
        var predicate = QueryExecutor.AnyEquals(Member(nameof(Row.Tags)), ["csharp"], typeof(string[]))!;

        ValueConstants(predicate).Should().BeEmpty();
    }

    [Fact]
    public void A_captured_predicate_still_matches_in_memory()
    {
        var parameter = Expression.Parameter(typeof(Row), "x");
        var predicate = QueryExecutor.AnyEquals(Expression.Property(parameter, nameof(Row.Name)), ["alice"], typeof(string))!;
        var matches = Expression.Lambda<Func<Row, bool>>(predicate, parameter).Compile();

        matches(new Row { Name = "alice" }).Should().BeTrue();
        matches(new Row { Name = "bob" }).Should().BeFalse();
    }

    [Fact]
    public void The_helper_reads_a_field_off_a_constant_holder()
    {
        var captured = SparkExpressions.Captured("alice", typeof(string));

        captured.Type.Should().Be(typeof(string));
        captured.Should().BeAssignableTo<MemberExpression>()
            .Which.Expression.Should().BeOfType<ConstantExpression>();
        Expression.Lambda<Func<string>>(captured).Compile()().Should().Be("alice");
    }
}
