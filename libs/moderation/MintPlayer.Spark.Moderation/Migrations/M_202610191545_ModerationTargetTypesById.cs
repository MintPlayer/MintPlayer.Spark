using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Moderation.Migrations;

/// <summary>
/// Moderation documents store their target's type by id, no longer by name (D28, commerce X9): votes, reputation
/// events, flags, review cases, locks and the audit log. The names are mapped through the composed model this
/// application serves, so a renamed type maps to the id the name had before the rename only if the migration runs
/// first: deploy it before (or with) the first rename. The work is <see cref="ModerationTargetTypeUpgrade"/>.
/// </summary>
/// <remarks>
/// A stored name the model does not know (a type since removed) is left as it is and logged: no type can be
/// guessed for it, and the review queue then shows the case without a link, as for any target the caller cannot open.
/// </remarks>
public partial class M_202610191545_ModerationTargetTypesById : ISparkMigration
{
    public static string? Description => "Moderation: store each target's type by id, not by name (D28)";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ILogger<M_202610191545_ModerationTargetTypesById> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var types = modelLoader.GetEntityTypes().ToList();
        var unknown = await ModerationTargetTypeUpgrade.RunAsync(
            store,
            ModerationTargetTypeUpgrade.NameMap(types),
            ModerationTargetTypeUpgrade.ClrSimpleNameMap(types),
            cancellationToken);

        foreach (var (collection, value) in unknown)
            logger.LogWarning("Moderation: {Collection} holds the target type '{Type}', which no model type has; left as it is.", collection, value);
    }
}
