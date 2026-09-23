using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK019: <c>HasValue</c> in an expression RavenDB translates becomes a field name and 500s.
/// </summary>
public class RavenHasValueAnalyzerTests
{
    private const string AnalyzerName = "RavenHasValueAnalyzer";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source]);

    private const string Preamble = """
        using System;
        using System.Linq;
        using System.Linq.Expressions;

        namespace TestApp;

        public class Commit { public int? PullRequestNumber { get; set; } }
        """;

    [Fact]
    public async Task HasValue_in_a_row_filter_expression_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class CommitActions
            {
                public Expression<Func<Commit, bool>> GetRowFilter()
                    => c => c.PullRequestNumber.HasValue;
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK019").Should().HaveCount(1,
            "a row filter is translated by the provider, so HasValue becomes PullRequestNumber_HasValue");
    }

    [Fact]
    public async Task HasValue_in_a_custom_query_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class CommitActions
            {
                public IQueryable<Commit> Recent(IQueryable<Commit> source)
                    => source.Where(c => c.PullRequestNumber.HasValue);
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK019").Should().HaveCount(1);
    }

    /// <summary>
    /// The replacement the message recommends must itself be clean, or the rule sends people in a
    /// circle.
    /// </summary>
    [Fact]
    public async Task The_recommended_replacement_is_clean()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class CommitActions
            {
                public Expression<Func<Commit, bool>> GetRowFilter()
                    => c => c.PullRequestNumber != null;
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK019").Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ Ordinary in-memory C# is untouched, which is the great majority of <c>HasValue</c> uses. A
    /// rule that fired on all of them would be turned off.
    /// </summary>
    [Fact]
    public async Task HasValue_in_ordinary_code_is_not_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            public class CommitActions
            {
                public bool IsPullRequest(Commit c) => c.PullRequestNumber.HasValue;

                public int Count(System.Collections.Generic.List<Commit> commits)
                    => commits.Count(c => c.PullRequestNumber.HasValue);
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK019").Should().BeEmpty(
            "an in-memory HasValue is correct and idiomatic; only a translated one breaks");
    }

    /// <summary>A <c>HasValue</c> property on some other type is not a nullable's.</summary>
    [Fact]
    public async Task An_unrelated_HasValue_property_is_not_flagged()
    {
        var diagnostics = await RunAsync("""
            using System;
            using System.Linq.Expressions;

            namespace TestApp;

            public class Box { public bool HasValue { get; set; } }

            public class Actions
            {
                public Expression<Func<Box, bool>> Filter() => b => b.HasValue;
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK019").Should().BeEmpty();
    }
}
