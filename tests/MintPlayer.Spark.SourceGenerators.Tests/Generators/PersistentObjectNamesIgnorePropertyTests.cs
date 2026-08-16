using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Pins that <c>[IgnoreProperty]</c> (#254) keeps a property out of the generated
/// <c>AttributeNames</c> constants. Without this, <c>AttributeNames.Person.InternalToken</c>
/// keeps compiling against an attribute the model no longer has — the lookup silently resolves
/// to nothing at runtime instead of failing at build time, which is the whole point of the
/// generated constants.
/// </summary>
public class PersistentObjectNamesIgnorePropertyTests
{
    private const string GeneratorName = "PersistentObjectNamesGenerator";

    private const string Source = """
        namespace MintPlayer.Spark.Actions
        {
            public abstract class DefaultPersistentObjectActions<T> { }
        }
        namespace MintPlayer.Spark.Abstractions
        {
            [System.AttributeUsage(System.AttributeTargets.Property)]
            public sealed class IgnorePropertyAttribute : System.Attribute { }
        }
        namespace TestApp
        {
            public class Person
            {
                public string? Id { get; set; }
                public string FirstName { get; set; } = "";

                [MintPlayer.Spark.Abstractions.IgnoreProperty]
                public string InternalToken { get; set; } = "";
            }

            public class PersonActions : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<Person> { }
        }
        """;

    [Fact]
    public void Ignored_property_gets_no_AttributeNames_constant()
    {
        var result = GeneratorHarness.Run(GeneratorName, [Source], rootNamespace: "TestApp");

        var names = result.GeneratedSources.FirstOrDefault(s => s.HintName == "AttributeNames.g.cs");
        names.Source.Should().NotBeNull();
        names.Source.Should().Contain("FirstName", "unignored properties still get constants");
        names.Source.Should().NotContain("InternalToken", "[IgnoreProperty] takes the property out of the model");
    }
}
