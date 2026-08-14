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

/// <summary>
/// A natural-id entity (id derived from <see cref="Code"/>) with no row rule. Used to exercise the
/// create-collision gate (security sweep H2): a second "create" replaying an existing Code must be
/// treated as an edit, not a silent overwrite under the New right.
/// </summary>
public class GuardedCoded : IHasNaturalId
{
    public string? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;

    public static string GetId(string code) => $"GuardedCodeds/{code.ToUpperInvariant()}";
    string IHasNaturalId.GetId() => GetId(Code);
}

/// <summary>Allows creation and reading, denies Edit — so a natural-id create-collision that the
/// framework re-routes through the Edit path is observably refused (security sweep H2).</summary>
public class GuardedCodedActions : DefaultPersistentObjectActions<GuardedCoded>
{
    public GuardedCodedActions(IEntityMapper entityMapper) : base(entityMapper) { }
    public override Task<bool> IsAllowedAsync(string action, GuardedCoded entity)
        => Task.FromResult(action != "Edit");
}

public class GuardedContext : SparkContext
{
    public IRavenQueryable<GuardedDoc> Docs => Session.Query<GuardedDoc>();
    public IRavenQueryable<GuardedCoded> Codeds => Session.Query<GuardedCoded>();
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
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "IsVisible", DataType = "bool" },
            ],
        }
    };
}

public static class GuardedCodedModel
{
    public static EntityTypeFile For(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "GuardedCoded",
            ClrType = typeof(GuardedCoded).FullName!,
            Breadcrumb = "{Code}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Code", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Payload", DataType = "string" },
            ],
        }
    };
}
