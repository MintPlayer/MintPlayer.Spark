using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Shared test entity whose <see cref="GuardedDocActions"/> enforces a row-level policy based
/// on <see cref="IsVisible"/>. Lets two unrelated test classes exercise the IsAllowedAsync
/// pipeline (DatabaseAccess row-level filter + Execute.cs parent-fetch gate) against the same
/// <c>{entityName}Actions</c> discovery rule — see <see cref="MintPlayer.Spark.Services.ActionsResolver"/>.
/// </summary>
public class GuardedDoc
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
}

public class GuardedDocActions : DefaultPersistentObjectActions<GuardedDoc>
{
    public GuardedDocActions(IEntityMapper entityMapper) : base(entityMapper) { }
    public override Task<bool> IsAllowedAsync(string action, GuardedDoc entity)
        => Task.FromResult(entity.IsVisible);
}

public class GuardedContext : SparkContext
{
    public IRavenQueryable<GuardedDoc> Docs => Session.Query<GuardedDoc>();
}

public static class GuardedDocModel
{
    public static EntityTypeFile For(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "GuardedDoc",
            ClrType = typeof(GuardedDoc).FullName!,
            DisplayAttribute = "Name",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "IsVisible", DataType = "bool" },
            ],
        }
    };
}
