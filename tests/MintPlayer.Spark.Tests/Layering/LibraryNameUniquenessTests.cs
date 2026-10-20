using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// S0 / D28: the libraries one application can compose together must not share a persistent-object name
/// (startup refuses: SPARK057) or a CLR simple name (RavenDB keys the default collection by the simple name, so
/// two such classes silently share one collection: SPARK062). This test holds every layered library this test
/// process references, which is every library in the repository, to that rule, so a new library (commerce,
/// ticketing, warehouse) that takes a natural name another library already has fails here, not in an
/// application.
/// </summary>
public class LibraryNameUniquenessTests
{
    private static IReadOnlyList<SparkComposedType> LibraryTypes()
        => SparkModelFiles.ComposeLenient(SparkLayerCatalog.Libraries, modelDirectory: null).Types;

    [Fact]
    public void The_libraries_compose_without_a_name_collision()
    {
        var (_, problems) = SparkModelFiles.ComposeLenient(SparkLayerCatalog.Libraries, modelDirectory: null);

        problems.Select(p => p.Message).Should().NotContain(m => m.Contains("SPARK057"),
            "two libraries naming one type cannot be composed into one application without a rename");
    }

    [Fact]
    public void Persistent_object_names_are_unique_ignoring_case()
    {
        var duplicates = LibraryTypes()
            .Where(t => t.Name is not null)
            .GroupBy(t => t.Name!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.Library))}")
            .ToList();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Clr_simple_names_of_library_types_are_unique()
    {
        var duplicates = LibraryTypes()
            .Select(t => (t.Library, ClrType: JsonNode.Parse(t.Json)?["persistentObject"]?["clrType"]?.GetValue<string>()))
            .Where(t => t.ClrType is not null)
            .GroupBy(t => SimpleName(t.ClrType!), StringComparer.Ordinal)
            .Where(g => g.Select(t => t.ClrType).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => $"{t.ClrType} ({t.Library})"))}")
            .ToList();

        duplicates.Should().BeEmpty("two classes with one simple name share a RavenDB collection, ids included");
    }

    [Fact]
    public void Each_clr_type_is_mapped_by_one_library_type()
    {
        var duplicates = LibraryTypes()
            .Select(t => (t.Library, t.Name, ClrType: JsonNode.Parse(t.Json)?["persistentObject"]?["clrType"]?.GetValue<string>()))
            .Where(t => t.ClrType is not null)
            .GroupBy(t => t.ClrType!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => $"{t.Name} ({t.Library})"))}")
            .ToList();

        duplicates.Should().BeEmpty("Spark resolves AsDetail rows, references and mapping by CLR type, first match wins");
    }

    private static string SimpleName(string clrType)
    {
        var name = clrType[(clrType.LastIndexOf('.') + 1)..];
        return name[(name.LastIndexOf('+') + 1)..];
    }
}
