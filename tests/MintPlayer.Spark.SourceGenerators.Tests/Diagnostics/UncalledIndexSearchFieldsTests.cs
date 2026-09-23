using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK018: the generator emits <c>Index(...)</c> calls into a private method, and until this rule
/// nothing checked that the constructor invokes it. The failure is invisible — the index deploys,
/// reports healthy and returns correct row counts while search matches nothing.
/// </summary>
public class UncalledIndexSearchFieldsTests
{
    private const string AnalyzerName = "SortCompanionAnalyzer";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source],
            referenceTypes:
            [
                typeof(FromIndexAttribute),
                typeof(SearchAttribute),
                typeof(AbstractIndexCreationTask<>),
                typeof(SparkIndexCreationTask<>),
            ]);

    private const string Preamble = """
        using MintPlayer.Spark;
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents.Indexes;
        using System;

        namespace TestApp;

        public class Car { public string? Model { get; set; } public DateTimeOffset Registered { get; set; } }
        """;

    [Fact]
    public async Task A_search_field_whose_constructor_never_applies_it_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().HaveCount(1);
    }

    [Fact]
    public async Task Calling_it_from_the_constructor_is_clean()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                    IndexSearchFields();
                }

                private void IndexSearchFields() { }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ The trap that would make this rule permanently silent. The generated partial declares
    /// <c>IndexSearchFields</c> itself, so widening the identifier scan from the constructors to the
    /// class would always match. Here the method exists on the class and is still never called.
    /// </summary>
    [Fact]
    public async Task Declaring_the_method_without_calling_it_is_still_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                }

                private void IndexSearchFields() { }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().HaveCount(1,
            "the method being declared is not the same as it being called");
    }

    /// <summary>One constructor calling it and another not is precisely the hole.</summary>
    [Fact]
    public async Task A_second_constructor_that_does_not_call_it_is_flagged()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                    IndexSearchFields();
                }

                public Cars_Overview(bool variant)
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                }

                private void IndexSearchFields() { }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().HaveCount(1);
    }

    /// <summary>
    /// A <c>DateTimeOffset</c> needs the call even with no <c>[Search]</c> anywhere, and the
    /// consequence is worse: without its <c>FieldIndexing.No</c> wrapper, Corax parks the whole index
    /// at <c>state=Error, entries=0</c>.
    /// </summary>
    [Fact]
    public async Task A_DateTimeOffset_field_needs_the_call_even_with_no_search_field()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public DateTimeOffset Registered { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Registered = c.Registered };
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().HaveCount(1);
    }

    [Fact]
    public async Task An_index_with_nothing_to_configure_is_clean()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                public string? Model { get; set; }
            }

            public partial class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model };
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().BeEmpty();
    }

    /// <summary>The base class supplies the call site, so there is no constructor call to demand.</summary>
    [Fact]
    public async Task An_index_on_the_Spark_base_class_is_exempt()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public partial class Cars_Overview : SparkIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().BeEmpty(
            "ConfigureSparkFields is called from CreateIndexDefinition, so demanding a constructor call " +
            "would be demanding the duplicate declaration that throws at deploy");
    }

    /// <summary>A non-partial index gets nothing generated at all; SPARK_INDEX_001 covers that case.</summary>
    [Fact]
    public async Task A_non_partial_index_is_left_to_its_own_rule()
    {
        var diagnostics = await RunAsync(Preamble + """

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
                public string? ModelSort { get; set; }
            }

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from c in cars select new VCar { Model = c.Model, ModelSort = c.Model };
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK018").Should().BeEmpty();
    }
}
