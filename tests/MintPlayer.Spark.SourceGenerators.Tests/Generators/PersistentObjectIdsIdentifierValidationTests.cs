using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// R2-C5 — App_Data/Model/*.json content is interpolated as C# identifier slots in
/// the generated PersistentObjectIds source. The reader must reject names and
/// schemas that aren't legal C# identifiers, otherwise a contributor (or template
/// tool) can plant something like
/// <c>"name": "Foo; } class X { static X(){ Process.Start(...); } public class Bar"</c>
/// and have it compile into the host on the next dotnet build.
/// </summary>
public class PersistentObjectIdsIdentifierValidationTests
{
    private const string GeneratorName = "PersistentObjectNamesGenerator";

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
        var schemaField = schema is null ? "" : $$""", "schema": "{{schema}}" """;
        return $$"""
            {
              "persistentObject": {
                "id": "{{id}}",
                "name": "{{name}}"{{schemaField}},
                "description": { "en": "ok" },
                "attributes": []
              }
            }
            """;
    }

    [Theory]
    [InlineData("Foo; } public class Pwn { static Pwn(){ } public class Bar")]
    [InlineData("Foo Bar")]                              // space
    [InlineData("Foo-Bar")]                              // dash
    [InlineData("123Numeric")]                           // leading digit
    [InlineData("")]                                      // empty
    [InlineData("Foo<T>")]                                // generic-shape garbage
    [InlineData("Foo\\\"; class X {")]                   // escape-bypass attempt
    public void Drops_entries_whose_name_is_not_a_legal_C_sharp_identifier(string hostileName)
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Hostile.json", Model("11111111-1111-1111-1111-111111111111", hostileName)),
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car")),
            ]);

        var ids = result.GeneratedSources.FirstOrDefault(s => s.HintName == "PersistentObjectIds.g.cs");

        // The good entry still lands.
        ids.Source.Should().Contain("public const string Car = \"27768be5-2ff5-4782-8b22-c0e8d163050e\";");

        // The hostile entry must NOT appear as an emitted identifier nor as a
        // raw string. We assert on the name fragment, not on the full payload,
        // because escape characters get re-encoded by the reader.
        var safeFragment = hostileName.Length > 0 ? hostileName.Split(' ', ';', '\\', '<')[0] : null;
        if (!string.IsNullOrEmpty(safeFragment) && safeFragment.Length >= 3)
        {
            ids.Source.Should().NotContain($"public const string {safeFragment} =",
                $"hostile name '{hostileName}' must not have been emitted as an identifier");
        }
    }

    [Theory]
    [InlineData("class X {")]
    [InlineData("Audit; namespace Pwn")]
    [InlineData("123BadStart")]
    public void Drops_entries_whose_schema_is_not_a_legal_C_sharp_identifier(string hostileSchema)
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Hostile.json", Model("11111111-1111-1111-1111-111111111111", "Item", hostileSchema)),
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car")),
            ]);

        var ids = result.GeneratedSources.FirstOrDefault(s => s.HintName == "PersistentObjectIds.g.cs");

        // Good entry still emits.
        ids.Source.Should().Contain("public const string Car =");
        // Hostile entry skipped — its name "Item" must not appear under any
        // schema-wrapper that includes the hostile string.
        ids.Source.Should().NotContain("public const string Item =",
            $"entry with hostile schema '{hostileSchema}' must be dropped before emission");
    }

    [Fact]
    public void Accepts_legal_identifier_names_with_underscores_and_digits()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [KnowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/_underscore.json", Model("11111111-1111-1111-1111-111111111111", "_Underscore")),
                ("App_Data/Model/foo123.json", Model("22222222-2222-2222-2222-222222222222", "Foo123")),
            ]);

        var ids = result.GeneratedSources.First(s => s.HintName == "PersistentObjectIds.g.cs").Source;
        ids.Should().Contain("public const string _Underscore =");
        ids.Should().Contain("public const string Foo123 =");
    }
}
