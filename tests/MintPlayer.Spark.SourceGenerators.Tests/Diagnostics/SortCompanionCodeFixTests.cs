using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// Issue #270, scoped to hand-written indexes (docs/datetimeoffset_query_sort_filter_PRD.md §9):
/// the widened SPARK006 (T35), and the code fixes for SPARK005 (T34) and SPARK006 (T36).
/// </summary>
/// <remarks>
/// Every fix test re-runs the analyzer over the FIXED documents and requires them to compile: a fix
/// that swaps SPARK005 for SPARK006 — property added, assignment forgotten — is worse than none.
/// </remarks>
public class SortCompanionCodeFixTests
{
    private const string AnalyzerName = "SortCompanionAnalyzer";
    private const string CodeFixName = "SortCompanionCodeFixProvider";

    /// <summary>Enough of RavenDB for lambda maps, reduces and multi-maps to compile.</summary>
    private const string RavenStub = """
        namespace Raven.Client.Documents.Indexes;
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq.Expressions;

        public enum FieldIndexing { No, Default, Search, Exact }
        public enum FieldStorage { No, Yes }

        public abstract class AbstractIndexCreationTask<TDocument, TReduceResult>
        {
            protected Expression<Func<IEnumerable<TDocument>, IEnumerable>> Map { get; set; } = null!;
            protected Expression<Func<IEnumerable<TReduceResult>, IEnumerable>> Reduce { get; set; } = null!;
            protected void Index(string field, FieldIndexing indexing) { }
            protected void Index(Expression<Func<TReduceResult, object?>> field, FieldIndexing indexing) { }
            protected void StoreAllFields(FieldStorage storage) { }
        }

        public abstract class AbstractIndexCreationTask<TDocument> : AbstractIndexCreationTask<TDocument, TDocument> { }
        """;

    private const string Entity = """
        using System;

        namespace TestApp;

        public class Car
        {
            public string LicensePlate { get; set; } = "";
            public string? Model { get; set; }
            public string? Code { get; set; }
            public DateTimeOffset RegisteredAt { get; set; }
            public DateTimeOffset? SoldAt { get; set; }
        }
        """;

    private static readonly Type[] References = [typeof(FromIndexAttribute), typeof(SparkIndexValue<>)];

    private static Task<CodeFixResult> FixAsync(string diagnosticId, string index, string projection)
        => CodeFixHarness.RunAnalyzerFixAsync(
            AnalyzerName, CodeFixName, diagnosticId,
            [FixtureProject.Of("TestApp",
                ("Raven.cs", RavenStub), ("Car.cs", Entity), ("Index.cs", index), ("VCar.cs", projection))],
            referenceTypes: References);

    private static Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string index, string projection)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [RavenStub, Entity, index, projection], referenceTypes: References);

    /// <summary>The fixed sources compile, and neither companion rule is left behind.</summary>
    private static async Task AssertCleanAfterFixAsync(CodeFixResult result)
    {
        var after = await AnalyzeAsync(result.Document("Index.cs"), result.Document("VCar.cs"));
        after.Where(d => d.Id is "SPARK005" or "SPARK006").Should().BeEmpty();

        var tree = (string text) => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Fixed",
            [tree(RavenStub), tree(Entity), tree(result.Document("Index.cs")), tree(result.Document("VCar.cs"))],
            GeneratorHarness.BuildReferences(References),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // --- T34: SPARK005 ----------------------------------------------------------------------------

    [Fact]
    public async Task T34_a_search_field_moves_its_analysis_to_a_mapped_Search_companion()
    {
        var result = await FixAsync("SPARK005",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => from car in cars
                                      select new
                                      {
                                          car.LicensePlate,
                                          car.Model,
                                      };
                        Index(nameof(VCar.Model), FieldIndexing.Search);
                        StoreAllFields(FieldStorage.Yes);
                    }
                }
                """,
            projection: """
                using MintPlayer.Spark.Abstractions;

                namespace TestApp;

                [FromIndex(typeof(Cars_Overview))]
                public class VCar
                {
                    public string LicensePlate { get; set; } = "";
                    public string? Model { get; set; }
                }
                """);

        result.OfferedTitles.Should().ContainSingle();
        result.Document("VCar.cs").Should().Contain("[IgnoreProperty] public string? ModelSearch { get; set; }");
        result.Document("Index.cs").Should().Contain("ModelSearch = car.Model");
        result.Document("Index.cs").Should().Contain("Index(nameof(VCar.ModelSearch), FieldIndexing.Search);");
        await AssertCleanAfterFixAsync(result);
    }

    [Fact]
    public async Task T34_an_exact_string_gets_a_mapped_Sort_companion_and_keeps_its_declaration()
    {
        var result = await FixAsync("SPARK005",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => cars.Select(car => new VCar { LicensePlate = car.LicensePlate, Code = car.Code });
                        Index("Code", FieldIndexing.Exact);
                    }
                }
                """,
            projection: """
                namespace TestApp;

                [MintPlayer.Spark.Abstractions.FromIndex(typeof(Cars_Overview))]
                public class VCar
                {
                    public string LicensePlate { get; set; } = "";
                    public string? Code { get; set; }
                }
                """);

        // No `using MintPlayer.Spark.Abstractions` in that file, so the attribute is written in full.
        result.Document("VCar.cs").Should().Contain(
            "[global::MintPlayer.Spark.Abstractions.IgnoreProperty] public string? CodeSort { get; set; }");
        result.Document("Index.cs").Should().Contain("Code = car.Code, CodeSort = car.Code");
        result.Document("Index.cs").Should().Contain("Index(\"Code\", FieldIndexing.Exact);");
        await AssertCleanAfterFixAsync(result);
    }

    // --- T35: the widened SPARK006 ----------------------------------------------------------------

    private const string RawProjection = """
        using System;
        using MintPlayer.Spark.Abstractions;

        namespace TestApp;

        [FromIndex(typeof(Cars_Overview))]
        public class VCar
        {
            public string LicensePlate { get; set; } = "";
            public DateTimeOffset RegisteredAt { get; set; }
            [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? RegisteredAtRaw { get; set; }
            public DateTimeOffset? SoldAt { get; set; }
            [IgnoreProperty] public SparkIndexValue<DateTimeOffset?>? SoldAtRaw { get; set; }
        }
        """;

    /// <summary>
    /// The exact shape measured in SP5: the Raw companion is declared and even named in an
    /// <c>Index(..., No)</c> call, which is a declaration, not an assignment. That index deploys healthy
    /// and returns every offset as +00:00. Reported on the base field.
    /// </summary>
    [Fact]
    public async Task T35_an_unmapped_Raw_companion_is_flagged_on_its_base_field_even_when_Index_names_it()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Linq;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from car in cars select new { car.LicensePlate, car.RegisteredAt, car.SoldAt };
                    Index(nameof(VCar.RegisteredAtRaw), FieldIndexing.No);
                    Index(nameof(VCar.SoldAtRaw), FieldIndexing.No);
                }
            }
            """, RawProjection);

        var spark006 = diagnostics.Where(d => d.Id == "SPARK006").ToList();
        spark006.Should().HaveCount(2);
        spark006.Any(d => d.GetMessage().Contains("'RegisteredAtRaw'") && d.GetMessage().Contains("+00:00")).Should().BeTrue();
        spark006.Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))
            .Should().BeEquivalentTo(["RegisteredAt", "SoldAt"]);
    }

    [Fact]
    public async Task T35_mapped_companions_and_lookalike_domain_properties_are_clean()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Linq;
            using Raven.Client.Documents.Indexes;

            namespace TestApp;

            public class Cars_Overview : AbstractIndexCreationTask<Car>
            {
                public Cars_Overview()
                {
                    Map = cars => from car in cars
                                  select new
                                  {
                                      car.LicensePlate,
                                      car.RegisteredAt,
                                      RegisteredAtRaw = new MintPlayer.Spark.Abstractions.SparkIndexValue<System.DateTimeOffset> { V = car.RegisteredAt },
                                      car.SoldAt,
                                      SoldAtRaw = new MintPlayer.Spark.Abstractions.SparkIndexValue<System.DateTimeOffset?> { V = car.SoldAt },
                                  };
                }
            }
            """, RawProjection.Replace(
                "public string LicensePlate { get; set; } = \"\";",
                // Ends in "Search" and has a base property, but is not a companion: no [IgnoreProperty].
                "public string LicensePlate { get; set; } = \"\";\n    public string? LicensePlateSearch { get; set; }"));

        diagnostics.Where(d => d.Id is "SPARK005" or "SPARK006").Should().BeEmpty();
    }

    /// <summary>
    /// The case the old rule could never report: the generator declared both companions on a partial
    /// pair, so they live in a <c>.g.cs</c> tree, and a diagnostic placed there is dropped. The widened
    /// rule reports on the hand-written base fields instead.
    /// </summary>
    [Fact]
    public async Task T35_generated_companions_the_hand_written_map_forgets_are_flagged_on_the_base_fields()
    {
        var result = await GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            "GenerateIndexGenerator",
            [GeneratorHarness.CreateAnalyzer(AnalyzerName)],
            [("Pair.cs", """
                using System;
                using System.Linq;
                using MintPlayer.Spark.Abstractions;
                using Raven.Client.Documents.Indexes;

                namespace TestApp.Data;

                public class Car
                {
                    public string? Id { get; set; }
                    public string? Model { get; set; }
                    public DateTimeOffset RegisteredAt { get; set; }
                }

                public partial class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => from car in cars select new VCar { Model = car.Model, RegisteredAt = car.RegisteredAt };
                        IndexSearchFields();
                    }
                }

                [FromIndex(typeof(Cars_Overview))]
                public partial class VCar
                {
                    [Search] public string? Model { get; set; }
                    public DateTimeOffset RegisteredAt { get; set; }
                }
                """)],
            referenceTypes:
            [
                typeof(FromIndexAttribute),
                typeof(GenerateIndexAttribute),
                typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
            ],
            rootNamespace: "TestApp",
            outputKind: OutputKind.ConsoleApplication);

        var vcar = result.Compilation.GetTypeByMetadataName("TestApp.Data.VCar");
        vcar!.GetMembers("ModelSearch").Should().ContainSingle("the generator declares the Search companion");
        vcar.GetMembers("RegisteredAtRaw").Should().ContainSingle("the generator declares the Raw companion");

        var spark006 = result.Diagnostics.Where(d => d.Id == "SPARK006").ToList();
        spark006.Should().HaveCount(2);
        spark006.All(d => d.Location.SourceTree!.FilePath == "Pair.cs").Should().BeTrue(
            "a diagnostic located in the generated .g.cs tree would be dropped");
    }

    // --- T36: the SPARK006 fix --------------------------------------------------------------------

    [Fact]
    public async Task T36_the_fix_maps_a_Raw_companion_with_the_generators_text_and_its_nullability()
    {
        var result = await FixAsync("SPARK006",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => from car in cars
                                      let sold = car.SoldAt
                                      select new
                                      {
                                          car.LicensePlate,
                                          car.RegisteredAt,
                                          SoldAt = sold,
                                          SoldAtRaw = new MintPlayer.Spark.Abstractions.SparkIndexValue<System.DateTimeOffset?> { V = sold },
                                      };
                    }
                }
                """,
            projection: RawProjection);

        var index = result.Document("Index.cs");
        index.Should().Contain(
            "RegisteredAtRaw = new global::MintPlayer.Spark.Abstractions.SparkIndexValue<global::System.DateTimeOffset> { V = car.RegisteredAt },");
        // On its own line, indented like the member it follows.
        var lines = index.Split('\n');
        var after = Array.FindIndex(lines, l => l.TrimEnd().EndsWith("car.RegisteredAt,"));
        lines[after + 1].TrimStart().Should().StartWith("RegisteredAtRaw = ");
        (lines[after + 1].Length - lines[after + 1].TrimStart().Length)
            .Should().Be(lines[after].Length - lines[after].TrimStart().Length);
        await AssertCleanAfterFixAsync(result);
    }

    [Fact]
    public async Task T36_a_nullable_DateTimeOffset_gets_a_nullable_wrapper()
    {
        var result = await FixAsync("SPARK006",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => cars.Select(car => new
                        {
                            car.LicensePlate,
                            car.RegisteredAt,
                            RegisteredAtRaw = new MintPlayer.Spark.Abstractions.SparkIndexValue<System.DateTimeOffset> { V = car.RegisteredAt },
                            Sold = car.SoldAt,
                            SoldAt = car.SoldAt,
                        });
                    }
                }
                """,
            projection: RawProjection);

        result.Document("Index.cs").Should().Contain(
            "SoldAtRaw = new global::MintPlayer.Spark.Abstractions.SparkIndexValue<global::System.DateTimeOffset?> { V = car.SoldAt }");
        await AssertCleanAfterFixAsync(result);
    }

    /// <summary>
    /// A map + reduce is refused: a map-only edit still compiles, so the compiler would not catch the
    /// map and the reduce disagreeing about the result. The warning stands; no action is offered.
    /// </summary>
    [Fact]
    public async Task T36_a_map_reduce_index_gets_the_warning_but_no_fix()
    {
        var result = await FixAsync("SPARK006",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public class Cars_Overview : AbstractIndexCreationTask<Car, VCar>
                {
                    public Cars_Overview()
                    {
                        Map = cars => from car in cars select new { car.LicensePlate, car.RegisteredAt, car.SoldAt };
                        Reduce = results => from r in results
                                            group r by r.LicensePlate into g
                                            select new { LicensePlate = g.Key, RegisteredAt = g.First().RegisteredAt, SoldAt = g.First().SoldAt };
                    }
                }
                """,
            projection: RawProjection);

        result.Diagnostics.Should().NotBeEmpty();
        result.OfferedTitles.Should().BeEmpty();
    }

    [Fact]
    public async Task T36_a_helper_projection_gets_the_warning_but_no_fix()
    {
        var result = await FixAsync("SPARK006",
            index: """
                using System.Linq;
                using Raven.Client.Documents.Indexes;

                namespace TestApp;

                public static class Helpers
                {
                    public static object Project(Car car) => new { car.LicensePlate, car.RegisteredAt, car.SoldAt };
                }

                public class Cars_Overview : AbstractIndexCreationTask<Car>
                {
                    public Cars_Overview()
                    {
                        Map = cars => cars.Select(car => Helpers.Project(car));
                    }
                }
                """,
            projection: RawProjection);

        result.Diagnostics.Should().NotBeEmpty();
        result.OfferedTitles.Should().BeEmpty();
    }
}
