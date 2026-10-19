using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;
using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Commerce X19 (reference-data Q-R6): libraries declare index maps with <c>[SparkIndexMap]</c> and ship no index
/// class; the application's generator emits one multi-map index per declared name, from every library that
/// contributes to it. Owner rule kept: the index classes live in the application.
/// </summary>
public class DeclaredIndexGeneratorTests
{
    private const string GeneratorName = "DeclaredIndexGenerator";

    private static readonly Type[] References =
    [
        typeof(SparkIndexMapAttribute),
        typeof(SparkMultiMapIndexCreationTask<>),
        typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
        typeof(System.Linq.Expressions.Expression),
    ];

    /// <summary>The documents and the shared result type, as a reference-data contracts library would ship them.</summary>
    private const string ContractsSource = """
        using MintPlayer.Spark.Abstractions;

        namespace RefData.Contracts;

        public class Place
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string[] Keys { get; set; } = [];
        }

        public class PostalLocality
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public class TypeaheadEntry
        {
            [Fuzzy] public string Token { get; set; } = string.Empty;
            [Search] public string Label { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
        }
        """;

    private const string PlacesLibrarySource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;
        using RefData.Contracts;

        namespace RefData.Places;

        public static class PlaceIndexes
        {
            [SparkIndexMap("RefLocalities_Typeahead", StoreAllFields = true)]
            public static Expression<System.Func<IEnumerable<Place>, IEnumerable<TypeaheadEntry>>> Places { get; } =
                places => from place in places
                          from key in place.Keys
                          select new TypeaheadEntry { Token = key, Label = place.Name, Kind = "place" };
        }
        """;

    private const string PostalLibrarySource = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using MintPlayer.Spark.Abstractions;
        using RefData.Contracts;

        namespace RefData.Postal;

        public static class PostalIndexes
        {
            [SparkIndexMap("RefLocalities_Typeahead")]
            public static readonly Expression<System.Func<IEnumerable<PostalLocality>, IEnumerable<TypeaheadEntry>>> Localities =
                localities => from locality in localities
                              select new TypeaheadEntry { Token = locality.Name, Label = locality.Name, Kind = "postal" };
        }
        """;

    private static GeneratorRunResult RunApp(
        OutputKind outputKind = OutputKind.ConsoleApplication,
        string appSource = "namespace RefApp; public class Program { public static void Main() { } }",
        params string[] librarySources)
    {
        var contracts = GeneratorHarness.CompileToMetadataReference("RefData.Contracts", [ContractsSource], References);
        var libraries = librarySources
            .Select((source, i) => GeneratorHarness.CompileToMetadataReference(
                $"RefData.Lib{i}", [source], References, additionalReferences: [contracts]))
            .ToList();

        return GeneratorHarness.Run(
            GeneratorName,
            [appSource],
            referenceTypes: References,
            rootNamespace: "RefApp", outputKind: outputKind,
            additionalReferences: [contracts, .. libraries]);
    }

    private static IReadOnlyList<Diagnostic> ErrorsIn(GeneratorRunResult result, string hintName)
        => result.FinalCompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith(hintName, StringComparison.Ordinal) == true)
            .ToList();

    [Fact]
    public void Maps_from_two_libraries_become_one_multi_map_index_in_the_application()
    {
        var result = RunApp(librarySources: [PlacesLibrarySource, PostalLibrarySource]);

        var (hintName, generated) = result.GeneratedSources.Should().ContainSingle().Which;

        generated.Should().Contain("namespace RefApp.Indexes");
        generated.Should().Contain("public partial class RefLocalities_Typeahead : global::MintPlayer.Spark.SparkMultiMapIndexCreationTask<global::RefData.Contracts.TypeaheadEntry>");
        generated.Should().Contain("AddMap<global::RefData.Contracts.Place>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::RefData.Places.PlaceIndexes.Places));");
        generated.Should().Contain("AddMap<global::RefData.Contracts.PostalLocality>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::RefData.Postal.PostalIndexes.Localities));");
        // Ordinal member order, whatever order the references arrive in.
        generated.IndexOf("RefData.Places.PlaceIndexes", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("RefData.Postal.PostalIndexes", StringComparison.Ordinal));
        ErrorsIn(result, hintName).Should().BeEmpty();
    }

    [Fact]
    public void Field_options_and_the_engine_come_from_the_result_type()
    {
        var generated = RunApp(librarySources: [PlacesLibrarySource, PostalLibrarySource]).GeneratedSources.Single().Source;

        generated.Should().Contain("Index(nameof(global::RefData.Contracts.TypeaheadEntry.Label), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
        generated.Should().NotContain("TypeaheadEntry.Token), global::Raven.Client.Documents.Indexes.FieldIndexing.Search");
        // One map asked for stored fields: the index stores them.
        generated.Should().Contain("StoreAllFields(global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
        // TypeaheadEntry.Token is [Fuzzy] (commerce X18).
        generated.Should().Contain("SearchEngineType = global::Raven.Client.Documents.Indexes.SearchEngineType.Lucene;");
        generated.Should().Contain("partial void OnInitialize();");
    }

    [Fact]
    public void A_class_library_emits_no_index()
    {
        var result = RunApp(OutputKind.DynamicallyLinkedLibrary, "namespace RefLib; public class Marker { }", PlacesLibrarySource);

        result.GeneratedSources.Should().BeEmpty("indexes always live in the application");
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_map_declared_in_the_application_itself_is_emitted_too()
    {
        var result = RunApp(appSource: """
            using System.Collections.Generic;
            using System.Linq;
            using System.Linq.Expressions;
            using MintPlayer.Spark.Abstractions;
            using RefData.Contracts;

            namespace RefApp;

            public class Program { public static void Main() { } }

            internal static class LocalIndexes
            {
                [SparkIndexMap("Places_ByName")]
                internal static Expression<System.Func<IEnumerable<Place>, IEnumerable<TypeaheadEntry>>> ByName =>
                    places => from place in places select new TypeaheadEntry { Token = place.Name, Label = place.Name };
            }
            """);

        var (hintName, generated) = result.GeneratedSources.Single();
        generated.Should().Contain("public partial class Places_ByName : global::MintPlayer.Spark.SparkMultiMapIndexCreationTask<global::RefData.Contracts.TypeaheadEntry>");
        generated.Should().Contain("AddMap<global::RefData.Contracts.Place>(global::MintPlayer.Spark.SparkIndexMaps.Widen(global::RefApp.LocalIndexes.ByName));");
        ErrorsIn(result, hintName).Should().BeEmpty();
    }

    [Fact]
    public void A_map_that_is_not_static_is_reported_and_skipped()
    {
        var result = RunApp(appSource: """
            using System.Collections.Generic;
            using System.Linq;
            using System.Linq.Expressions;
            using MintPlayer.Spark.Abstractions;
            using RefData.Contracts;

            namespace RefApp;

            public class Program { public static void Main() { } }

            public class LocalIndexes
            {
                [SparkIndexMap("Places_ByName")]
                public Expression<System.Func<IEnumerable<Place>, IEnumerable<TypeaheadEntry>>> ByName =>
                    places => from place in places select new TypeaheadEntry { Token = place.Name };
            }
            """);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK_INDEX_015")
            .Which.GetMessage().Should().Contain("not static");
    }

    [Fact]
    public void A_map_of_the_wrong_shape_is_reported()
    {
        var result = RunApp(appSource: """
            using System.Collections.Generic;
            using MintPlayer.Spark.Abstractions;

            namespace RefApp;

            public class Program { public static void Main() { } }

            public static class LocalIndexes
            {
                [SparkIndexMap("Places_ByName")]
                public static string ByName => "from place in docs.Places select new { place.Name }";

                [SparkIndexMap("Not a name")]
                public static System.Linq.Expressions.Expression<System.Func<IEnumerable<RefData.Contracts.Place>, IEnumerable<string>>> Odd =>
                    places => System.Linq.Enumerable.Select(places, p => p.Name);
            }
            """);

        var messages = result.GeneratorDiagnostics.Where(d => d.Id == "SPARK_INDEX_015").Select(d => d.GetMessage()).ToList();
        messages.Should().HaveCount(2);
        messages.Should().Contain(m => m.Contains("Expression<Func<IEnumerable<TSource>, IEnumerable<TResult>>>"));
        messages.Should().Contain(m => m.Contains("not a valid C# class name"));
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Maps_of_one_index_that_disagree_on_the_result_type_are_reported()
    {
        var result = RunApp(appSource: """
            using System.Collections.Generic;
            using System.Linq;
            using System.Linq.Expressions;
            using MintPlayer.Spark.Abstractions;
            using RefData.Contracts;

            namespace RefApp;

            public class Program { public static void Main() { } }

            public sealed class OtherEntry { public string Token { get; set; } = string.Empty; }

            public static class LocalIndexes
            {
                [SparkIndexMap("Shared_Index")]
                public static Expression<System.Func<IEnumerable<Place>, IEnumerable<TypeaheadEntry>>> A =>
                    places => from place in places select new TypeaheadEntry { Token = place.Name };

                [SparkIndexMap("Shared_Index")]
                public static Expression<System.Func<IEnumerable<PostalLocality>, IEnumerable<OtherEntry>>> B =>
                    localities => from locality in localities select new OtherEntry { Token = locality.Name };
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK_INDEX_016")
            .Which.GetMessage().Should().Contain("RefApp.LocalIndexes.B");
        var generated = result.GeneratedSources.Single().Source;
        generated.Should().Contain("AddMap<global::RefData.Contracts.Place>");
        generated.Should().NotContain("AddMap<global::RefData.Contracts.PostalLocality>");
    }
}
