using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK066 (#536 O18): a relational store keeps no <c>DateTimeKind</c> and reads every <c>DateTime</c> back as UTC,
/// so a stored <c>DateTime</c> gets a note steering new code to <c>DateTimeOffset</c> or <c>DateOnly</c>.
/// </summary>
public class RelationalDateTimeAnalyzerTests
{
    private static Task<IReadOnlyList<Diagnostic>> RunAsync(params string[] sources)
        => GeneratorHarness.RunAnalyzerAsync("RelationalDateTimeAnalyzer", sources, [typeof(SparkContext)]);

    /// <summary>What a project referencing a relational provider sees: the dialect type.</summary>
    private const string RelationalStore = """
        namespace MintPlayer.Spark.Relational
        {
            public abstract class SparkRelationalDialect { }
        }
        """;

    private const string Model = """
        using System;
        using System.Linq;
        using MintPlayer.Spark;

        namespace TestApp
        {
            public class AppContext : SparkContext
            {
                public IQueryable<Car> Cars { get; set; } = null!;
            }

            public abstract class Entity
            {
                public DateTime CreatedOn { get; set; }
            }

            public class Car : Entity
            {
                public string? Id { get; set; }
                public DateTime? ServicedOn { get; set; }
                public DateTimeOffset SoldAt { get; set; }
                public DateOnly BuiltOn { get; set; }
                public static DateTime Epoch { get; set; }
            }

            public class Unstored
            {
                public DateTime When { get; set; }
            }
        }
        """;

    [Fact]
    public async Task A_stored_DateTime_gets_a_note_when_a_relational_store_is_referenced()
    {
        var notes = (await RunAsync(Model, RelationalStore)).Where(d => d.Id == "SPARK066").ToList();

        notes.Select(d => d.GetMessage()).Should().HaveCount(2)
            .And.Contain(m => m.Contains("Car.ServicedOn"))
            .And.Contain(m => m.Contains("Entity.CreatedOn"), "a base type's property is stored with the document");
        notes.Should().AllSatisfy(d => d.Severity.Should().Be(DiagnosticSeverity.Info));
        notes.Should().AllSatisfy(d => d.Location.IsInSource.Should().BeTrue());
    }

    [Fact]
    public async Task Without_a_relational_store_a_DateTime_keeps_its_kind_and_gets_no_note()
    {
        var diagnostics = await RunAsync(Model);

        diagnostics.Where(d => d.Id == "SPARK066").Should().BeEmpty();
    }
}
