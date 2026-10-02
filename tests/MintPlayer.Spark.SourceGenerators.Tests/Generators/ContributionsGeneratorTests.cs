using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using VerifyXunit;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The contributions generator (contributions M4), which ships inside the MintPlayer.Spark.Contributions
/// package rather than with the other Spark generators: Verify snapshots of the generated API, and
/// compile-and-run checks of the ids and the row key against the PRD's exact strings.
/// </summary>
public class ContributionsGeneratorTests
{
    internal const string GeneratorName = "ContributionsGenerator";
    internal const string GeneratorAssembly = "MintPlayer.Spark.Contributions.SourceGenerators";

    /// <summary>What every fixture compiles against; SoftDelete only when a test asks for it.</summary>
    internal static Type[] References(bool softDelete) => softDelete
        ? [typeof(ContributionAttribute), typeof(IHasNaturalId), typeof(ValueKeyAttribute), typeof(Newtonsoft.Json.JsonIgnoreAttribute), typeof(ISoftDeletable)]
        : [typeof(ContributionAttribute), typeof(IHasNaturalId), typeof(ValueKeyAttribute), typeof(Newtonsoft.Json.JsonIgnoreAttribute)];

    internal static string SongSource(string attribution, string elementKind = "class") => $$"""
        using System.Collections.Generic;
        using MintPlayer.Spark.Contributions;

        namespace TestApp;

        // The owner needs no 'partial': nothing is added to it.
        public class Song
        {
            public string? Id { get; set; }
            public string Title { get; set; } = "";

            [Contribution(Attribution = {{attribution}})]
            [Newtonsoft.Json.JsonIgnore]
            public List<Lyrics> Lyrics { get; set; } = new();
        }

        public partial {{elementKind}} Lyrics
        {
            [ContributionSlot] public string Language { get; set; } = "";
            [ContributionSlot] public string Script { get; set; } = "";
            public string Text { get; set; } = "";
        }
        """;

    private const string ChartSource = """
        using MintPlayer.Spark.Contributions;

        namespace TestApp.Charts;

        public enum Region { World, Europe, Asia }

        public class Chart
        {
            public string? Id { get; set; }

            [Contribution]
            [Newtonsoft.Json.JsonIgnore]
            public Ranking[] Rankings { get; set; } = [];
        }

        public partial class Ranking
        {
            [ContributionSlot] public int Year { get; set; }
            [ContributionSlot] public Region Region { get; set; }
            [ContributionSlot] public System.Guid Edition { get; set; }
            [ContributionSlot] public bool Live { get; set; }
            public int Position { get; set; }
            public string? Note { get; set; }
        }
        """;

    private const string BiographySource = """
        using MintPlayer.Spark.Contributions;

        namespace TestApp.People;

        public class Artist
        {
            public string? Id { get; set; }

            [Contribution(Attribution = ContributionAttribution.UpdatedAt)]
            [Newtonsoft.Json.JsonIgnore]
            public Biography? Biography { get; set; }
        }

        public partial class Biography
        {
            public string Text { get; set; } = "";
        }
        """;

    private static GeneratorRunResult Run(string source, bool softDelete)
        => GeneratorHarness.Run(GeneratorName, [source], referenceTypes: References(softDelete),
            rootNamespace: "TestApp", generatorAssemblyName: GeneratorAssembly);

    private static string Render(GeneratorRunResult result)
    {
        result.GeneratorDiagnostics.Should().BeEmpty();
        var errors = result.FinalCompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.Should().BeEmpty();
        // The generated files must also compile without warnings (nullable ones included).
        result.FinalCompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning
                        && d.Location.SourceTree?.FilePath.EndsWith(".Contribution.g.cs", StringComparison.Ordinal) == true)
            .Should().BeEmpty();

        return result.GeneratedSources.Count == 0
            ? "<no generated sources>"
            : string.Join("\n\n", result.GeneratedSources.Select(s => $"=== {s.HintName} ===\n{s.Source}"));
    }

    [Fact]
    public Task Song_lyrics_two_string_slots_without_attribution_or_soft_delete()
        => Verifier.Verify(Render(Run(SongSource("ContributionAttribution.None"), softDelete: false)));

    [Fact]
    public Task Song_lyrics_with_all_attribution_and_soft_delete()
        => Verifier.Verify(Render(Run(
            SongSource("ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt | ContributionAttribution.History"),
            softDelete: true)));

    [Fact]
    public Task Int_enum_guid_and_bool_slots_on_an_array()
        => Verifier.Verify(Render(Run(ChartSource, softDelete: false)));

    [Fact]
    public Task Single_valued_property_without_slots()
        => Verifier.Verify(Render(Run(BiographySource, softDelete: false)));

    [Fact]
    public void A_record_element_gets_the_same_key_and_registration()
    {
        var result = Run(SongSource("ContributionAttribution.None", elementKind: "record"), softDelete: false);
        Render(result);

        var element = result.GeneratedSources.Single(s => s.HintName.EndsWith(".Element.Contribution.g.cs", StringComparison.Ordinal)).Source;
        element.Should().Contain("partial record Lyrics");
        element.Should().Contain("public string Key =>");
    }

    [Fact]
    public void An_app_partial_that_is_already_soft_deletable_is_not_given_the_members_twice()
    {
        const string appPartial = """
            namespace TestApp;

            public partial class SongLyricsContribution : MintPlayer.Spark.SoftDelete.ISoftDeletable
            {
                public bool IsDeleted { get; set; }
                public System.DateTimeOffset? DeletedAt { get; set; }
                public string? DeletedBy { get; set; }
                public string? DeleteReason { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(GeneratorName, [SongSource("ContributionAttribution.None"), appPartial],
            referenceTypes: References(softDelete: true), rootNamespace: "TestApp", generatorAssemblyName: GeneratorAssembly);

        Render(result);
        var declaration = result.GeneratedSources.Single(s => s.HintName == "TestApp.SongLyrics.Contribution.g.cs").Source;
        declaration.Should().NotContain("public bool IsDeleted");
        declaration.Should().Contain("public override bool IsSoftDeletable => true;");
    }

    [Fact]
    public void A_declaration_with_an_error_generates_nothing()
    {
        // Not partial (SPARK031): the generator must not emit a Key the element cannot receive.
        var result = Run(SongSource("ContributionAttribution.None").Replace("public partial class Lyrics", "public class Lyrics"), softDelete: false);
        result.GeneratedSources.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ compile and run

    private static Assembly CompileAndLoad(string source, bool softDelete)
    {
        var analyzer = (DiagnosticAnalyzer)GeneratorHarness.InstantiateComponent("ContributionsAnalyzer", GeneratorAssembly, typeof(DiagnosticAnalyzer));
        var run = GeneratorHarness.RunGeneratorThenAnalyzersAsync(GeneratorName, [analyzer], [("Fixture.cs", source)],
            referenceTypes: References(softDelete), rootNamespace: "TestApp", generatorAssemblyName: GeneratorAssembly).GetAwaiter().GetResult();

        using var stream = new MemoryStream();
        var emit = run.Compilation.WithAssemblyName("ContributionsRun" + Guid.NewGuid().ToString("N")).Emit(stream);
        emit.Success.Should().BeTrue(string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }

    [Fact]
    public void Generated_ids_and_key_match_the_PRD()
    {
        var assembly = CompileAndLoad(SongSource("ContributionAttribution.Contributor | ContributionAttribution.History"), softDelete: true);
        var contributionType = assembly.GetType("TestApp.SongLyricsContribution", throwOnError: true)!;
        var currentType = assembly.GetType("TestApp.SongLyricsCurrent", throwOnError: true)!;
        var lyricsType = assembly.GetType("TestApp.Lyrics", throwOnError: true)!;
        var songType = assembly.GetType("TestApp.Song", throwOnError: true)!;

        contributionType.GetMethod("GetId")!.Invoke(null, ["Songs/1234", "ko", "Kore", "MintPlayerUsers/abc"])
            .Should().Be("Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc");
        currentType.GetMethod("GetId")!.Invoke(null, ["Songs/1234", "ko", "Kore"])
            .Should().Be("Songs/1234/Lyrics/ko/Kore");

        var lyrics = Activator.CreateInstance(lyricsType)!;
        lyricsType.GetProperty("Language")!.SetValue(lyrics, "ko");
        lyricsType.GetProperty("Script")!.SetValue(lyrics, "Kore");
        lyricsType.GetProperty("Text")!.SetValue(lyrics, "line 1\nline 2");
        lyricsType.GetProperty("Key")!.GetValue(lyrics).Should().Be("ko/Kore");
        lyricsType.GetProperty("Key")!.GetCustomAttribute<ValueKeyAttribute>().Should().NotBeNull();
        lyricsType.GetProperty("Key")!.CanWrite.Should().BeFalse();

        // The module initializer registered the row key and the descriptor.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        SparkValueObjects.IsKeyed(lyricsType).Should().BeTrue();
        SparkValueObjects.GetKeyPropertyName(lyricsType).Should().Be("Key");
        SparkValueObjects.GetKey(lyrics).Should().Be("ko/Kore");

        var descriptor = ContributionRegistry.Find(songType, "Lyrics");
        descriptor.Should().NotBeNull();
        descriptor!.ContributionType.Should().Be(contributionType);
        descriptor.CurrentType.Should().Be(currentType);
        descriptor.ContributionsQueryName.Should().Be("SongLyricsContributions");
        descriptor.SlotNames.Should().Equal("Language", "Script");
        descriptor.ValueNames.Should().Equal("Text");
        descriptor.AttributionAttributeNames.Should().Equal("ContributorName", "ContributionCount");
        descriptor.IsSoftDeletable.Should().BeTrue();
        descriptor.ContributionPrefix("Songs/1234").Should().Be("Songs/1234/LyricsContributions/");
        descriptor.CurrentPrefix("Songs/1234").Should().Be("Songs/1234/Lyrics/");
        descriptor.ShapeHash.Should().MatchRegex("^[0-9a-f]{16}$");

        // element -> contribution -> current -> row, through the typed descriptor.
        var d = descriptor.GetType();
        var updatedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var contribution = d.GetMethod("CreateContribution")!.Invoke(descriptor, ["Songs/1234", lyrics, "MintPlayerUsers/abc", updatedAt])!;
        contributionType.GetProperty("Id")!.GetValue(contribution).Should().Be("Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc");
        ((IHasNaturalId)contribution).GetId().Should().Be("Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc");
        contribution.Should().BeAssignableTo<ISoftDeletable>();
        d.GetMethod("SlotContributionPrefix")!.Invoke(descriptor, ["Songs/1234", lyrics])
            .Should().Be("Songs/1234/LyricsContributions/ko/Kore/User/");

        var current = d.GetMethod("CreateCurrent")!.Invoke(descriptor, [contribution, 4])!;
        currentType.GetProperty("Id")!.GetValue(current).Should().Be("Songs/1234/Lyrics/ko/Kore");
        ((IHasNaturalId)current).GetId().Should().Be("Songs/1234/Lyrics/ko/Kore");
        ((ICurrentContribution)current).ContributionId.Should().Be("Songs/1234/LyricsContributions/ko/Kore/User/MintPlayerUsers/abc");
        currentType.GetProperty("ContributionCount")!.GetValue(current).Should().Be(4);

        var row = d.GetMethod("CreateRow")!.Invoke(descriptor, [current, "Alice"])!;
        lyricsType.GetProperty("Text")!.GetValue(row).Should().Be("line 1\nline 2");
        lyricsType.GetProperty("Key")!.GetValue(row).Should().Be("ko/Kore");
        lyricsType.GetProperty("ContributorName")!.GetValue(row).Should().Be("Alice");
        lyricsType.GetProperty("ContributionCount")!.GetValue(row).Should().Be(4);
        lyricsType.GetProperty("ContributorName")!.CanWrite.Should().BeFalse();

        // The target's rows, read and replaced through the descriptor.
        var song = Activator.CreateInstance(songType)!;
        d.GetMethod("SetRows")!.Invoke(descriptor, [song, CreateList(lyricsType, row)]);
        ((System.Collections.IList)songType.GetProperty("Lyrics")!.GetValue(song)!).Count.Should().Be(1);
        ((System.Collections.ICollection)d.GetMethod("GetRows")!.Invoke(descriptor, [song])!).Count.Should().Be(1);

        d.GetMethod("FindInvalidSlot")!.Invoke(descriptor, [lyrics]).Should().BeNull();
        lyricsType.GetProperty("Script")!.SetValue(lyrics, "Ko/re");
        d.GetMethod("FindInvalidSlot")!.Invoke(descriptor, [lyrics]).Should().Be("Script");
    }

    [Fact]
    public void Non_string_slots_format_invariantly()
    {
        var assembly = CompileAndLoad(ChartSource, softDelete: false);
        var contributionType = assembly.GetType("TestApp.Charts.ChartRankingsContribution", throwOnError: true)!;
        var region = Enum.Parse(assembly.GetType("TestApp.Charts.Region", throwOnError: true)!, "Europe");
        var edition = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        var previous = CultureInfo.CurrentCulture;
        try
        {
            // sv-SE formats negative numbers with U+2212 under ICU; the id must not.
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            contributionType.GetMethod("GetId")!.Invoke(null, ["Charts/1", -5, region, edition, true, "Users/1"])
                .Should().Be("Charts/1/RankingsContributions/-5/Europe/0f8fad5bd9cb469fa16570867728950e/true/User/Users/1");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        ContributionSlotFormat.IsValid("0f8fad5bd9cb469fa16570867728950e").Should().BeTrue();
        ContributionSlotFormat.IsValid(new string('a', 33)).Should().BeFalse();
        ContributionSlotFormat.IsValid("").Should().BeFalse();
        ContributionSlotFormat.IsValid("en-Latn").Should().BeTrue();
        ContributionSlotFormat.IsValid("a b").Should().BeFalse();
    }

    [Fact]
    public void Single_valued_ids_have_no_slot_segments()
    {
        var assembly = CompileAndLoad(BiographySource, softDelete: false);
        assembly.GetType("TestApp.People.ArtistBiographyContribution", throwOnError: true)!.GetMethod("GetId")!
            .Invoke(null, ["Artists/7", "Users/1"]).Should().Be("Artists/7/BiographyContributions/User/Users/1");
        assembly.GetType("TestApp.People.ArtistBiographyCurrent", throwOnError: true)!.GetMethod("GetId")!
            .Invoke(null, ["Artists/7"]).Should().Be("Artists/7/Biography");

        var biography = assembly.GetType("TestApp.People.Biography", throwOnError: true)!;
        biography.GetProperty("Key").Should().BeNull();
        biography.GetProperty("UpdatedAt").Should().NotBeNull();
        SparkValueObjects.IsKeyed(biography).Should().BeFalse();
    }

    private static System.Collections.IList CreateList(Type elementType, object item)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        list.Add(item);
        return list;
    }
}
