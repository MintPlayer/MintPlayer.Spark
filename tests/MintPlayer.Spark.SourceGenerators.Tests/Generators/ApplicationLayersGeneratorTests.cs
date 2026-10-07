using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Composition M3 (D1 amended by S1, D2, D16): an application records the referenced assemblies that
/// ship layers, in layer order, so the run time loads exactly those; two libraries with one alias are
/// SPARK043.
/// </summary>
public class ApplicationLayersGeneratorTests
{
    private const string GeneratorName = "ApplicationLayersGenerator";

    /// <summary>A compiled library carrying one layer and, optionally, the layered assemblies it stacks above.</summary>
    private static MetadataReference Library(string assemblyName, string alias, params string[] dependsOn)
    {
        var source = $"[assembly: MintPlayer.Spark.Abstractions.SparkLayer({SymbolDisplay.FormatLiteral(alias, quote: true)}, \"translations\", \"translations.json\", \"{{}}\")]";
        if (dependsOn.Length > 0)
            source += $"\n[assembly: MintPlayer.Spark.Abstractions.SparkLayerDependencies({string.Join(", ", dependsOn.Select(d => SymbolDisplay.FormatLiteral(d, quote: true)))})]";
        return GeneratorHarness.CompileToMetadataReference(assemblyName, [source], [typeof(SparkLayerAttribute)]);
    }

    private static GeneratorRunResult Run(OutputKind outputKind, params MetadataReference[] libraries)
        => GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            referenceTypes: [typeof(SparkLayerAttribute)],
            outputKind: outputKind,
            additionalReferences: libraries);

    private static IReadOnlyList<string> Recorded(GeneratorRunResult result)
        => GeneratorHarness.EmitAndLoad(
                "AppProbe" + Guid.NewGuid().ToString("N"),
                result.GeneratedSources.Select(s => s.Source),
                referenceTypes: [typeof(SparkLayerAttribute)])
            .GetCustomAttribute<SparkLayerAssembliesAttribute>()!
            .AssemblyNames;

    [Fact]
    public void The_layered_references_are_recorded_core_first_then_by_dependency_then_by_name()
    {
        // Alpha.Ext stacks above Zeta.Lib, so it comes after it although it sorts first by name;
        // Beta.Lib is unrelated to both, so only its name places it.
        var result = Run(OutputKind.ConsoleApplication,
            Library("Alpha.Ext", "alpha", "Zeta.Lib", "MintPlayer.Spark"),
            Library("Zeta.Lib", "zeta", "MintPlayer.Spark"),
            Library("Beta.Lib", "beta"),
            Library("MintPlayer.Spark", "spark"),
            GeneratorHarness.CompileToMetadataReference("Plain.Lib", ["public class Plain { }"]));

        result.GeneratorDiagnostics.Should().BeEmpty();
        Recorded(result).Should().Equal("MintPlayer.Spark", "Beta.Lib", "Zeta.Lib", "Alpha.Ext");
    }

    [Fact]
    public void A_dependency_on_an_assembly_the_application_does_not_reference_is_ignored()
    {
        var result = Run(OutputKind.ConsoleApplication, Library("Alpha.Ext", "alpha", "Missing.Lib"), Library("Beta.Lib", "beta"));

        Recorded(result).Should().Equal("Alpha.Ext", "Beta.Lib");
    }

    [Fact]
    public void A_library_records_nothing()
    {
        var result = Run(OutputKind.DynamicallyLinkedLibrary, Library("MintPlayer.Spark", "spark"));

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void An_application_without_layered_references_records_nothing()
    {
        var result = Run(OutputKind.ConsoleApplication, GeneratorHarness.CompileToMetadataReference("Plain.Lib", ["public class Plain { }"]));

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Two_libraries_with_one_alias_are_SPARK043_naming_both()
    {
        var result = Run(OutputKind.ConsoleApplication, Library("Acme.Auth", "auth"), Library("Other.Auth", "auth"));

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK043");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("'auth'").And.Contain("Acme.Auth").And.Contain("Other.Auth");
    }

    [Fact]
    public void The_generator_build_of_the_order_function_agrees_with_these_expectations()
    {
        // D14/Q3: one ordering function, linked into the generators and the run time. The run-time
        // build is checked against the same table in MintPlayer.Spark.Tests (SparkLibraryOrderTests).
        foreach (var (libraries, expected) in SparkLibraryOrderCases.All)
            GeneratorOrder(libraries).Should().Equal(expected);
    }

    /// <summary>The order function compiled into the generator assembly, by reflection: its types are internal.</summary>
    private static IReadOnlyList<string> GeneratorOrder((string Name, string[] DependsOn)[] libraries)
    {
        var sort = Assembly.Load("MintPlayer.Spark.SourceGenerators")
            .GetType("MintPlayer.Spark.Layering.SparkLibraryOrder", throwOnError: true)!
            .GetMethod("Sort", BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof((string Name, string[] DependsOn)));

        Func<(string Name, string[] DependsOn), string> name = l => l.Name;
        Func<(string Name, string[] DependsOn), IEnumerable<string>> dependsOn = l => l.DependsOn;
        var sorted = (IEnumerable<(string Name, string[] DependsOn)>)sort.Invoke(null, [libraries, name, dependsOn])!;
        return sorted.Select(l => l.Name).ToList();
    }
}

/// <summary>
/// The ordering cases both builds of <c>SparkLibraryOrder</c> are held to. Kept word for word in
/// <c>MintPlayer.Spark.Tests/Layering/SparkLibraryOrderTests.cs</c>.
/// </summary>
internal static class SparkLibraryOrderCases
{
    public static readonly ((string Name, string[] DependsOn)[] Libraries, string[] Expected)[] All =
    [
        // Core first, then ordinal by name between unrelated libraries (upper case sorts first).
        ([("b.Lib", []), ("MintPlayer.Spark", []), ("A.Lib", [])], ["MintPlayer.Spark", "A.Lib", "b.Lib"]),
        // A library comes after every library it depends on, whatever its name.
        ([("A.Ext", ["Z.Lib"]), ("Z.Lib", []), ("M.Lib", [])], ["M.Lib", "Z.Lib", "A.Ext"]),
        // Chains: C above B above A.
        ([("C", ["B", "A"]), ("A", []), ("B", ["A"])], ["A", "B", "C"]),
        // A dependency outside the set is ignored.
        ([("A", ["Gone"]), ("B", [])], ["A", "B"]),
        // A cycle cannot come from assembly references; it still terminates, by name.
        ([("B", ["A"]), ("A", ["B"])], ["A", "B"]),
    ];
}
