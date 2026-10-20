using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK063 (commerce X18): a <c>Fuzzy(...)</c> query against an index Spark deploys on the server default engine.
/// Corax refuses fuzzy queries at runtime, so the query compiles, deploys and throws on its first call.
/// </summary>
public class FuzzyQueryAnalyzerTests
{
    private const string AnalyzerName = "FuzzyQueryAnalyzer";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Analyze(string body, string types = DefaultTypes)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName,
        [
            $$"""
            using System.Linq;
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Indexes;
            using Raven.Client.Documents.Session;

            namespace TestApp;

            {{types}}

            public class Search
            {
                public object Run(IDocumentSession session, IDocumentQuery<Place> passedIn)
                {
                    {{body}}
                }
            }
            """,
        ],
        [typeof(FuzzyAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask), typeof(Raven.Client.Documents.Session.IDocumentSession)]);

    private const string DefaultTypes = """
        public class Place
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public class Places_ByName : AbstractIndexCreationTask<Place>
        {
            public Places_ByName() { Map = places => from p in places select new { p.Name }; }
        }

        public class Places_Fuzzy : AbstractIndexCreationTask<Place>
        {
            public Places_Fuzzy() { Map = places => from p in places select new { p.Name }; }
        }

        [FromIndex(typeof(Places_Fuzzy))]
        public class VPlace
        {
            [Fuzzy] public string Name { get; set; } = string.Empty;
        }

        public class Places_Pinned : AbstractIndexCreationTask<Place>
        {
            public Places_Pinned()
            {
                Map = places => from p in places select new { p.Name };
                SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
            }
        }
        """;

    [Fact]
    public async Task A_fuzzy_query_on_an_unmarked_index_is_flagged()
    {
        var diagnostics = await Analyze("""return session.Advanced.DocumentQuery<Place, Places_ByName>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063")
            .Which.GetMessage().Should().Contain("index 'Places_ByName'");
    }

    [Fact]
    public async Task A_projection_marking_a_field_fuzzy_marks_the_index()
    {
        var diagnostics = await Analyze("""return session.Advanced.DocumentQuery<Place, Places_Fuzzy>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_index_that_sets_Lucene_itself_is_not_flagged()
    {
        var diagnostics = await Analyze("""return session.Advanced.DocumentQuery<Place, Places_Pinned>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fuzzy_field_on_the_mapped_document_marks_the_index()
    {
        var diagnostics = await Analyze(
            """return session.Advanced.DocumentQuery<Place, Places_ByName>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""",
            """
            public class Place
            {
                public string? Id { get; set; }
                [Fuzzy] public string Name { get; set; } = string.Empty;
            }

            public class Places_ByName : AbstractIndexCreationTask<Place>
            {
                public Places_ByName() { Map = places => from p in places select new { p.Name }; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Fuzzy by default (2026-10-19): a searchable field alone puts a hand-written index on Lucene.</summary>
    [Fact]
    public async Task A_hand_written_searchable_field_marks_the_index()
    {
        var diagnostics = await Analyze(
            """return session.Advanced.DocumentQuery<Place, Places_Searchable>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""",
            DefaultTypes + """

            public class Places_Searchable : AbstractIndexCreationTask<Place>
            {
                public Places_Searchable()
                {
                    Map = places => from p in places select new { p.Name };
                    Index(x => x.Name, FieldIndexing.Search);
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Spark keeps a Corax pin on an index that is only searchable, so a fuzzy query on it still throws.</summary>
    [Fact]
    public async Task A_searchable_index_pinned_to_Corax_is_flagged()
    {
        var diagnostics = await Analyze(
            """return session.Advanced.DocumentQuery<Place, Places_SearchableCorax>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""",
            DefaultTypes + """

            public class Places_SearchableCorax : AbstractIndexCreationTask<Place>
            {
                public Places_SearchableCorax()
                {
                    Map = places => from p in places select new { p.Name };
                    Index(x => x.Name, FieldIndexing.Search);
                    SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
                }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063")
            .Which.GetMessage().Should().Contain("index 'Places_SearchableCorax'");
    }

    /// <summary><c>[Fuzzy(false)]</c> is the per-field opt-out of Spark's search; it never puts an index on Lucene.</summary>
    [Fact]
    public async Task An_opt_out_does_not_mark_the_index()
    {
        var diagnostics = await Analyze(
            """return session.Advanced.DocumentQuery<Place, Places_ByName>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""",
            """
            public class Place
            {
                public string? Id { get; set; }
                [Fuzzy(false)] public string Name { get; set; } = string.Empty;
            }

            public class Places_ByName : AbstractIndexCreationTask<Place>
            {
                public Places_ByName() { Map = places => from p in places select new { p.Name }; }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063");
    }

    [Fact]
    public async Task A_dynamic_fuzzy_query_runs_on_an_auto_index_and_is_flagged()
    {
        var diagnostics = await Analyze("""return session.Advanced.DocumentQuery<Place>().WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063")
            .Which.GetMessage().Should().Contain("auto index");
    }

    [Fact]
    public async Task An_index_named_by_string_is_resolved_to_its_class()
    {
        var flagged = await Analyze("""return session.Advanced.DocumentQuery<Place>("Places/ByName").WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");
        var marked = await Analyze("""return session.Advanced.DocumentQuery<Place>("Places/Fuzzy").WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        flagged.Should().ContainSingle(d => d.Id == "SPARK063");
        marked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_the_rule_cannot_follow_is_not_guessed_at()
    {
        var diagnostics = await Analyze("""return passedIn.WhereEquals("Name", "brusels").Fuzzy(0.8m).ToList();""");

        diagnostics.Should().BeEmpty("the query was built elsewhere; the rule never fires on an index it cannot name");
    }
}
