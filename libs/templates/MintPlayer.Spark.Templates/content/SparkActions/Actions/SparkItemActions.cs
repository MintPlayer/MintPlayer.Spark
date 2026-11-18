using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;

namespace SparkNs.Actions;

/// <summary>
/// SparkItem's actions. Spark finds the class by its name, SparkItemActions.
/// </summary>
/// <remarks>
/// <para>
/// Services come in through <c>[Inject]</c> readonly fields: the source generator writes the constructor, the
/// base class's services included. Do not write a constructor yourself.
/// </para>
/// <para>
/// Say which rows a caller sees. While SparkItem is granted to a well-known group (<c>@anonymous</c>,
/// <c>@authenticated</c>) in App_Data/security.json, startup refuses until this class does one of two things:
/// override <c>GetRowFilterAsync</c> (or <c>IsAllowedAsync</c>) to narrow the rows, or implement
/// <c>ISparkOwnsRowSecurity</c> and write down in <c>RowSecurityRationale</c> why every row may be shown.
/// </para>
/// <para>
/// Other hooks: <c>OnNewAsync</c> fills a new object's defaults, <c>OnRefreshAsync</c> answers an attribute that
/// triggers a refresh, <c>IAfterSave&lt;SparkItem&gt;</c> runs after a save, and <c>OnDeleteRowAsync</c> runs per
/// deleted row.
/// </para>
/// </remarks>
public partial class SparkItemActions : DefaultPersistentObjectActions<SparkItem>, IBeforeSave<SparkItem>
{
    [Inject] private readonly ILogger<SparkItemActions> logger;

    /// <summary>Runs before every SparkItem save: validate it or derive values here.</summary>
    public ValueTask OnBeforeSaveAsync(SparkItem entity, SaveContext context)
    {
        logger.LogDebug("Saving SparkItem {Id}", entity.Id);
        return ValueTask.CompletedTask;
    }
}
