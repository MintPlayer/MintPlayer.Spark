using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The <c>[Replicated]</c> generator (warehouse PRD §4.2.1): the replica class is the single declaration,
/// and its manifest, ETL script and hash are generated into it as constants.
/// </summary>
public class ReplicatedGeneratorTests
{
    private const string GeneratorName = "ReplicatedGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.LibraryGenerators";

    /// <summary>Union of both diagnostic sets: uncompilable generated code reports nothing of its own.</summary>
    private static GeneratorRunResult Run(params string[] sources)
        => GeneratorHarness.Run(
            GeneratorName,
            sources,
            referenceTypes: [typeof(ReplicatedAttribute), typeof(IgnorePropertyAttribute)],
            rootNamespace: "TestLib",
            generatorAssemblyName: GeneratorAssembly);

    private static void ShouldHaveNoErrors(GeneratorRunResult result)
        => result.GeneratorDiagnostics
            .Concat(result.FinalCompilationDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

    private static string Combined(GeneratorRunResult result)
        => string.Join("\n", result.GeneratedSources.Select(s => s.Source));

    /// <summary>The value of a generated <c>const string</c>, unescaped, read from the generated syntax.</summary>
    private static string Constant(GeneratorRunResult result, string name)
    {
        var declarator = result.GeneratedSources
            .SelectMany(s => CSharpSyntaxTree.ParseText(s.Source).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
            .Single(v => v.Identifier.ValueText == name);
        return ((LiteralExpressionSyntax)declarator.Initializer!.Value).Token.ValueText;
    }

    private static string Car(string attribute, string body) => $$"""
        using MintPlayer.Spark.Abstractions;
        using MintPlayer.Spark.Replication.Abstractions;

        namespace TestLib;

        {{attribute}}
        public partial class Car
        {
            public string? Id { get; set; }
        {{body}}
        }
        """;

    [Fact]
    public void Emits_constants_for_partial_class()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars")]""",
            """    public string LicensePlate { get; set; } = "";"""));

        ShouldHaveNoErrors(result);

        var combined = Combined(result);
        combined.Should().Contain("partial class Car : global::MintPlayer.Spark.Replication.Abstractions.IReplicatedEntity");
        combined.Should().Contain("public const string? ReplicationSourceModule = null;");
        combined.Should().Contain("public const bool ReplicationWriteBack = false;");
        combined.Should().Contain("[assembly: global::MintPlayer.Spark.Replication.Abstractions.SparkReplicaTypes(typeof(global::TestLib.Car))]");

        Constant(result, "ReplicationSourceCollection").Should().Be("Cars");
        Constant(result, "ReplicationTargetCollection").Should().Be("Cars");
        Constant(result, "ReplicationManifest").Should().Be("""{"v":1,"source":"Cars","target":"Cars","fields":[["LicensePlate","LicensePlate"]]}""");
        Constant(result, "ReplicationScript").Should().StartWith("loadToCars({\n    LicensePlate: this.LicensePlate,\n");
        Constant(result, "ReplicationHash").Should().StartWith("sha256:");
    }

    [Fact]
    public void Emits_module_and_write_back_when_given()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars", SourceModule = "Fleet", WriteBack = true)]""",
            """    public string LicensePlate { get; set; } = "";"""));

        ShouldHaveNoErrors(result);
        Constant(result, "ReplicationSourceModule").Should().Be("Fleet");
        Combined(result).Should().Contain("public const bool ReplicationWriteBack = true;");
    }

    [Fact]
    public void Maps_ReplicatedFrom_paths_null_safely()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars")]""",
            """
                [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }
                [ReplicatedFrom("Owner.Address.City")] public string? OwnerCity { get; set; }
                [ReplicatedFrom("Plate")] public string? LicensePlate { get; set; }
            """));

        ShouldHaveNoErrors(result);

        Constant(result, "ReplicationManifest").Should().Contain("""["OwnerName","Owner.Name"]""");
        Constant(result, "ReplicationManifest").Should().Contain("""["LicensePlate","Plate"]""");

        var script = Constant(result, "ReplicationScript");
        script.Should().Contain("OwnerName: (this.Owner == null ? null : this.Owner.Name),");
        script.Should().Contain("OwnerCity: (this.Owner == null ? null : (this.Owner.Address == null ? null : this.Owner.Address.City)),");
        script.Should().Contain("LicensePlate: this.Plate,");
        script.Should().Contain("'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector']");
    }

    [Fact]
    public void Skips_Id_and_getter_only_members()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars")]""",
            """
                public string Model { get; set; } = "";
                public string Computed => Model;
                public string GetOnly { get; } = "";
                public string PrivateSet { get; private set; } = "";
                public static string Static { get; set; } = "";
                internal string Internal { get; set; } = "";
                [IgnoreProperty] public string Ignored { get; set; } = "";
                public string this[int i] { get => ""; set { } }
                public string Initialized { get; init; } = "";
            """));

        ShouldHaveNoErrors(result);
        Constant(result, "ReplicationManifest").Should().Be(
            """{"v":1,"source":"Cars","target":"Cars","fields":[["Model","Model"],["Initialized","Initialized"]]}""");
    }

    [Fact]
    public void Hash_is_stable_and_changes_with_any_field()
    {
        const string attribute = """[Replicated(SourceCollection = "Cars")]""";
        const string body = """    public string Model { get; set; } = "";""";

        var first = Constant(Run(Car(attribute, body)), "ReplicationHash");
        var again = Constant(Run(Car(attribute, body)), "ReplicationHash");
        var added = Constant(Run(Car(attribute, body + "\n    public string Color { get; set; } = \"\";")), "ReplicationHash");
        var renamedPath = Constant(Run(Car(attribute, """    [ReplicatedFrom("Type")] public string Model { get; set; } = "";""")), "ReplicationHash");
        var otherSource = Constant(Run(Car("""[Replicated(SourceCollection = "Vehicles")]""", body)), "ReplicationHash");

        again.Should().Be(first);
        added.Should().NotBe(first);
        renamedPath.Should().NotBe(first);
        otherSource.Should().NotBe(first);
    }

    /// <summary>
    /// The owner re-renders a posted manifest with the runtime copy of the renderer and compares hashes
    /// (WD15). The generator's linked copy must agree byte for byte.
    /// </summary>
    [Fact]
    public void Hash_equals_runtime_renderer()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars", SourceModule = "Fleet")]""",
            """
                public string LicensePlate { get; set; } = "";
                [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }
            """));

        ShouldHaveNoErrors(result);

        var json = Constant(result, "ReplicationManifest");
        ReplicaScriptRenderer.TryParse(json, out var manifest, out var error).Should().BeTrue();
        error.Should().BeNull();

        ReplicaScriptRenderer.ToJson(manifest!).Should().Be(json);
        var script = ReplicaScriptRenderer.RenderScript(manifest!);
        script.Should().Be(Constant(result, "ReplicationScript"));
        ReplicaScriptRenderer.Hash(json, script).Should().Be(Constant(result, "ReplicationHash"));
    }

    [Theory]
    [InlineData("Car", "Cars")]
    [InlineData("Person", "People")]
    [InlineData("Company", "Companies")]
    [InlineData("Box", "Boxes")]
    [InlineData("Day", "Days")]
    [InlineData("Branch", "Branches")]
    [InlineData("SalesPerson", "SalesPeople")]
    public void Default_target_collection_pluralises(string className, string expected)
    {
        var result = Run($$"""
            using MintPlayer.Spark.Replication.Abstractions;

            namespace TestLib;

            [Replicated(SourceCollection = "Source")]
            public partial class {{className}}
            {
                public string Name { get; set; } = "";
            }
            """);

        ShouldHaveNoErrors(result);
        Constant(result, "ReplicationTargetCollection").Should().Be(expected);
        Constant(result, "ReplicationScript").Should().StartWith($"loadTo{expected}({{");
    }

    [Fact]
    public void Explicit_target_collection_wins()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars", TargetCollection = "FleetCars")]""",
            """    public string Model { get; set; } = "";"""));

        ShouldHaveNoErrors(result);
        Constant(result, "ReplicationTargetCollection").Should().Be("FleetCars");
        Constant(result, "ReplicationScript").Should().StartWith("loadToFleetCars({");
    }

    // ---------------------------------------------------------------- SPARK065

    [Fact]
    public void SPARK065_when_not_partial()
    {
        var result = Run("""
            using MintPlayer.Spark.Replication.Abstractions;

            namespace TestLib;

            [Replicated(SourceCollection = "Cars")]
            public class Car
            {
                public string Model { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK065");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void SPARK065_not_reported_for_a_partial_class()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars")]""",
            """    public string Model { get; set; } = "";"""));

        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK065");
    }

    [Fact]
    public void SPARK065_when_a_containing_type_is_not_partial_or_the_type_is_generic()
    {
        var nested = Run("""
            using MintPlayer.Spark.Replication.Abstractions;

            namespace TestLib;

            public class Outer
            {
                [Replicated(SourceCollection = "Cars")]
                public partial class Car { public string Model { get; set; } = ""; }
            }
            """);
        nested.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK065" && d.GetMessage().Contains("'Outer'"));
        nested.GeneratedSources.Should().BeEmpty();

        var generic = Run("""
            using MintPlayer.Spark.Replication.Abstractions;

            namespace TestLib;

            [Replicated(SourceCollection = "Cars")]
            public partial class Car<T> { public string Model { get; set; } = ""; }
            """);
        generic.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK065" && d.GetMessage().Contains("generic"));
        generic.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public async Task SPARK065_code_fix_adds_partial()
    {
        var result = await CodeFixHarness.RunGeneratorFixAsync(
            generatorTypeName: GeneratorName,
            codeFixTypeName: "ReplicatedPartialCodeFixProvider",
            diagnosticId: "SPARK065",
            projects:
            [
                FixtureProject.Of("TestLib", ("Car.cs", """
                    using MintPlayer.Spark.Replication.Abstractions;

                    namespace TestLib;

                    public class Outer
                    {
                        [Replicated(SourceCollection = "Cars")]
                        public sealed class Car
                        {
                            public string Model { get; set; } = "";
                        }
                    }
                    """)),
            ],
            referenceTypes: [typeof(ReplicatedAttribute)],
            generatorAssemblyName: GeneratorAssembly,
            codeFixAssemblyName: GeneratorAssembly);

        var document = result.Document("Car.cs");
        document.Should().Contain("public partial class Outer");
        document.Should().Contain("public sealed partial class Car");
    }

    // ---------------------------------------------------------------- SPARK066

    [Theory]
    [InlineData("""[Replicated(SourceCollection = "")]""", """    public string Model { get; set; } = "";""", "SourceCollection is missing or empty")]
    [InlineData("""[Replicated(SourceCollection = "Cars-v2")]""", """    public string Model { get; set; } = "";""", "'Cars-v2'")]
    [InlineData("""[Replicated(SourceCollection = "Cars", TargetCollection = "x y")]""", """    public string Model { get; set; } = "";""", "'x y'")]
    [InlineData("""[Replicated(SourceCollection = "Cars")]""", """    [ReplicatedFrom("Owner..Name")] public string? OwnerName { get; set; }""", "'Owner..Name'")]
    [InlineData("""[Replicated(SourceCollection = "Cars")]""", """    [ReplicatedFrom("loadDocument('x')")] public string? Evil { get; set; }""", "not a plain identifier")]
    [InlineData("""[Replicated(SourceCollection = "Cars")]""", """    public string Computed => "";""", "no property to replicate")]
    public void SPARK066_invalid_identifier_or_missing_SourceCollection(string attribute, string body, string expectedInMessage)
    {
        var result = Run(Car(attribute, body));

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK066" && d.GetMessage().Contains(expectedInMessage));
        result.GeneratedSources.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- SPARK067

    private const string FleetTypes = """
        namespace Fleet;

        public class Entity { public string? Id { get; set; } public string Model { get; set; } = ""; }
        public class Person { public string Name { get; set; } = ""; }
        public struct Mileage { public int Kilometres { get; set; } }
        public class Car : Entity
        {
            public string LicensePlate { get; set; } = "";
            public Person? Owner { get; set; }
            public Mileage? Odometer { get; set; }
        }
        """;

    [Fact]
    public void SPARK067_path_missing_on_OriginalType()
    {
        var result = Run(FleetTypes, Car(
            """[Replicated(SourceCollection = "Cars", OriginalType = typeof(Fleet.Car))]""",
            """
                public string LicensePlate { get; set; } = "";
                [ReplicatedFrom("Owner.Email")] public string? OwnerEmail { get; set; }
            """));

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK067" && d.GetMessage().Contains("'Owner.Email'"));
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void SPARK067_not_reported_when_every_path_exists_through_bases_and_nullables()
    {
        var result = Run(FleetTypes, Car(
            """[Replicated(SourceCollection = "Cars", OriginalType = typeof(Fleet.Car))]""",
            """
                public string LicensePlate { get; set; } = "";
                public string Model { get; set; } = "";
                [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }
                [ReplicatedFrom("Odometer.Kilometres")] public int? Kilometres { get; set; }
            """));

        ShouldHaveNoErrors(result);
    }

    // ---------------------------------------------------------------- SPARK068

    [Fact]
    public void SPARK068_handwritten_member_collides()
    {
        var result = Run(Car(
            """[Replicated(SourceCollection = "Cars")]""",
            """
                public string Model { get; set; } = "";
                public const string ReplicationScript = "loadToCars({ Model: this.Model });";
            """));

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK068" && d.GetMessage().Contains("'ReplicationScript'"));
        result.GeneratedSources.Should().BeEmpty();
    }
}
