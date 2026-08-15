using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A test entity with NO Actions class — <c>ActionsResolver</c> falls back to the unoverridden
/// <c>DefaultPersistentObjectActions{T}</c>, so <c>HasRowRule</c> is false. The counterpart to
/// <see cref="GuardedDoc"/>: tests that pin what the framework does when row security is
/// <em>absent</em> (e.g. the per-row <c>can</c> block staying null, #243).
/// </summary>
public class PlainDoc
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class PlainDocModel
{
    public static EntityTypeFile For(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "PlainDoc",
            ClrType = typeof(PlainDoc).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        }
    };
}
