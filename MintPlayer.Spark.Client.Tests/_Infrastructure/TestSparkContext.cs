using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Client.Tests._Infrastructure;

/// <summary>
/// Minimal SparkContext for the Client.Tests harness. Intentionally separate from
/// MintPlayer.Spark.Tests' TestSparkContext — Phase 3 of the PRD requires this project
/// not to reference MintPlayer.Spark.Tests, so the two harnesses can evolve independently.
/// </summary>
public sealed class TestSparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
}

public sealed class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public static class TestModels
{
    public static EntityTypeFile Person(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "Person",
            ClrType = typeof(Person).FullName!,
            DisplayAttribute = "LastName",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        }
    };
}
