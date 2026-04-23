using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Exercises the PersistentObjectIds emission path of
/// <c>PersistentObjectNamesGenerator</c>. Unlike <c>PersistentObjectNames</c>
/// which scans CLR <c>DefaultPersistentObjectActions&lt;T&gt;</c> types, this
/// path reads <c>App_Data/Model/*.json</c> AdditionalFiles and emits
/// schema-nested <c>const string</c> Guid values.
/// </summary>
public class PersistentObjectIdsGeneratorTests
{
    private const string GeneratorName = "PersistentObjectNamesGenerator";

    // A tiny CLR source that makes the generator's "knowsSpark" gate pass.
    // We reference DefaultPersistentObjectActions via a source stub so the generator
    // sees a symbol with the expected fully-qualified name.
    private const string KnowsSparkSource = """
        namespace MintPlayer.Spark.Actions
        {
            public abstract class DefaultPersistentObjectActions<T> { }
        }
        namespace TestApp
        {
            public class Dummy : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<object> { }
        }
        """;

    private static string Model(string id, string name, string? schema = null)
    {
        var schemaLine = schema is null ? "" : $"    \"schema\": \"{schema}\",\n";
        return "{\n"
             + "  \"persistentObject\": {\n"
             + $"    \"id\": \"{id}\",\n"
             + $"    \"name\": \"{name}\",\n"
             + schemaLine
             + $"    \"description\": {{ \"en\": \"{name}\" }},\n"
             + "    \"attributes\": [\n"
             + "      { \"id\": \"11111111-1111-1111-1111-111111111111\", \"name\": \"Foo\" }\n"
             + "    ]\n"
             + "  }\n"
             + "}\n";
    }

    [Fact]
    public void Emits_PersistentObjectIds_Under_Default_Schema_When_Schema_Missing()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car")),
                ("App_Data/Model/Person.json", Model("11111111-2222-3333-4444-555555555555", "Person")),
            ]);

        var ids = result.GeneratedSources.FirstOrDefault(s => s.HintName == "PersistentObjectIds.g.cs");
        ids.Source.Should().NotBeNull();
        ids.Source.Should().Contain("public static class PersistentObjectIds");
        ids.Source.Should().Contain("public static class Default");
        ids.Source.Should().Contain("public const string Car = \"27768be5-2ff5-4782-8b22-c0e8d163050e\";");
        ids.Source.Should().Contain("public const string Person = \"11111111-2222-3333-4444-555555555555\";");
    }

    [Fact]
    public void Emits_Schema_Nested_Classes_For_Multi_Schema_Input()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car", "Default")),
                ("App_Data/Model/AuditLog.json", Model("99999999-8888-7777-6666-555555555555", "AuditLog", "Audit")),
            ]);

        var ids = result.GeneratedSources.First(s => s.HintName == "PersistentObjectIds.g.cs").Source;
        ids.Should().Contain("public static class Default");
        ids.Should().Contain("public static class Audit");
        ids.Should().Contain("public const string Car = \"27768be5-2ff5-4782-8b22-c0e8d163050e\";");
        ids.Should().Contain("public const string AuditLog = \"99999999-8888-7777-6666-555555555555\";");
    }

    [Fact]
    public void Skips_Files_Outside_Model_Directory()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/translations.json", """{ "hello": "world" }"""),
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car")),
            ]);

        var ids = result.GeneratedSources.First(s => s.HintName == "PersistentObjectIds.g.cs").Source;
        ids.Should().Contain("Car");
        ids.Should().NotContain("hello"); // translations.json ignored
    }

    [Fact]
    public void Skips_Model_File_That_Is_Not_A_PersistentObject_Wrapper()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/NotAPo.json", """{ "somethingElse": { "id": "27768be5-2ff5-4782-8b22-c0e8d163050e" } }"""),
                ("App_Data/Model/Car.json", Model("11111111-2222-3333-4444-555555555555", "Car")),
            ]);

        var ids = result.GeneratedSources.First(s => s.HintName == "PersistentObjectIds.g.cs").Source;
        ids.Should().Contain("Car");
        ids.Should().NotContain("somethingElse");
    }

    [Fact]
    public void Skips_Model_File_With_Invalid_Guid()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Invalid.json", Model("not-a-guid", "Invalid")),
                ("App_Data/Model/Car.json", Model("11111111-2222-3333-4444-555555555555", "Car")),
            ]);

        var ids = result.GeneratedSources.First(s => s.HintName == "PersistentObjectIds.g.cs").Source;
        ids.Should().Contain("Car");
        ids.Should().NotContain("Invalid");
    }

    [Fact]
    public void Emits_Nothing_When_No_Model_Files_Present()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/translations.json", """{ "hello": "world" }"""),
            ]);

        result.GeneratedSources.Should().NotContain(s => s.HintName == "PersistentObjectIds.g.cs");
    }
}
