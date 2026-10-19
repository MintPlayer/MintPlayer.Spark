using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// X20 item 1: <c>PersistentObjectNames</c> holds the names the composed model declares — the same source
/// as <c>PersistentObjectIds</c> — and never CLR class names, and the unused <c>AttributeNames</c>
/// constants are gone. (Library types: <see cref="LibraryModelCompositionTests"/>.)
/// </summary>
public class PersistentObjectNamesGeneratorTests
{
    private const string GeneratorName = "PersistentObjectNamesGenerator";

    private const string Source = """
        namespace MintPlayer.Spark.Actions
        {
            public abstract class DefaultPersistentObjectActions<T> { }
        }
        namespace TestApp
        {
            public class Person
            {
                public string? Id { get; set; }
                public string FirstName { get; set; } = "";
            }

            public class PersonActions : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<Person> { }

            // A class with actions but no model file: not a persistent object of the model.
            public class Draft { public string? Id { get; set; } }
            public class DraftActions : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<Draft> { }
        }
        """;

    private static GeneratorRunResult Run()
        => GeneratorHarness.Run(
            GeneratorName,
            [Source],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Person.json", """{ "persistentObject": { "id": "27768be5-2ff5-4782-8b22-c0e8d163050e", "name": "Person" } }"""),
                // Virtual: no clrType, no class. Code names it by string (GetPersistentObjectAsync("Home")).
                ("App_Data/Model/Home.json", """{ "persistentObject": { "id": "2d6f4b10-9c27-4e83-b5a1-7f0c8e3d6a94", "name": "Home" } }"""),
            ],
            outputKind: OutputKind.ConsoleApplication);

    private static string Names(GeneratorRunResult result)
        => result.GeneratedSources.Single(s => s.HintName == "PersistentObjectNames.g.cs").Source;

    [Fact]
    public void A_model_type_gets_a_name_constant()
        => Names(Run()).Should().Contain("public const string Person = \"Person\";");

    [Fact]
    public void A_virtual_type_without_a_class_gets_a_name_constant()
        => Names(Run()).Should().Contain("public const string Home = \"Home\";");

    [Fact]
    public void A_class_the_model_does_not_declare_gets_no_name_constant()
        => Names(Run()).Should().NotContain("Draft", "the names are the model's, not the CLR classes'");

    [Fact]
    public void No_AttributeNames_constants_are_generated()
        => Run().GeneratedSources.Should().NotContain(s => s.HintName == "AttributeNames.g.cs",
            "nothing used them; nameof(Entity.Property) names an attribute with compile-time checking");
}
