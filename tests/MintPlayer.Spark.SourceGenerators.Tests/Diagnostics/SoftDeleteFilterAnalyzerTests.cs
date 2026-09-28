using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK022: <c>!x.IsDeleted</c> / <c>x.IsDeleted == false</c> in a translated expression drops every
/// document without the field (#460 spike S3).
/// </summary>
public class SoftDeleteFilterAnalyzerTests
{
    private const string AnalyzerName = "SoftDeleteFilterAnalyzer";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], referenceTypes: [typeof(System.Linq.Queryable)]);

    private const string Preamble = """
        using System;
        using System.Linq;
        using System.Linq.Expressions;

        namespace MintPlayer.Spark.SoftDelete
        {
            public interface ISoftDeletable { bool IsDeleted { get; set; } }
        }

        namespace TestApp
        {
            public class Order : MintPlayer.Spark.SoftDelete.ISoftDeletable { public bool IsDeleted { get; set; } }
            public class Plain { public bool IsDeleted { get; set; } }
        """;

    private const string Close = "\n}";

    [Theory]
    [InlineData("o => !o.IsDeleted")]
    [InlineData("o => o.IsDeleted == false")]
    [InlineData("o => false == o.IsDeleted")]
    [InlineData("o => !(o.IsDeleted)")]
    public async Task A_wrong_shape_in_a_translated_filter_is_flagged(string lambda)
    {
        var diagnostics = await RunAsync(Preamble + $$"""

            public class OrderActions
            {
                public Expression<Func<Order, bool>> GetRowFilter() => {{lambda}};
            }
            """ + Close);

        diagnostics.Where(d => d.Id == "SPARK022").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_wrong_shape_in_a_queryable_where_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class OrderQueries
            {
                public IQueryable<Order> Live(IQueryable<Order> source) => source.Where(o => !o.IsDeleted);
            }
            """ + Close);

        diagnostics.Where(d => d.Id == "SPARK022").Should().HaveCount(1);
    }

    [Fact]
    public async Task The_interface_member_itself_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class Policy
            {
                public Expression<Func<MintPlayer.Spark.SoftDelete.ISoftDeletable, bool>> Filter() => x => !x.IsDeleted;
            }
            """ + Close);

        diagnostics.Where(d => d.Id == "SPARK022").Should().HaveCount(1);
    }

    [Fact]
    public async Task The_recommended_shape_is_clean()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class OrderActions
            {
                public Expression<Func<Order, bool>> GetRowFilter() => o => o.IsDeleted != true;
            }
            """ + Close);

        diagnostics.Where(d => d.Id == "SPARK022").Should().BeEmpty();
    }

    [Fact]
    public async Task In_memory_code_and_unrelated_types_are_not_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class Elsewhere
            {
                public bool IsLive(Order o) => !o.IsDeleted;
                public int Count(Order[] orders) => orders.Count(o => !o.IsDeleted);
                public Expression<Func<Plain, bool>> NotSoftDeletable() => p => !p.IsDeleted;
            }
            """ + Close);

        diagnostics.Where(d => d.Id == "SPARK022").Should().BeEmpty();
    }
}
