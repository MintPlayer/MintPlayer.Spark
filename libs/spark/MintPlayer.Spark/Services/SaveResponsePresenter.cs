using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// What a create or an update answers with: the saved row <b>as a load presents it</b> to this caller
/// (contributions M2c-2b, PRD leak 2), never the posted object.
/// </summary>
/// <remarks>
/// <para>
/// The posted object is whatever the client sent plus whatever the save hooks wrote into it, and it
/// used to be returned as is. With the old per-row shield, which restored the stored value of a
/// protected attribute into the posted object before the merge, that echoed the very secret the load
/// had blanked. Re-reading through <see cref="IDatabaseAccess.GetPersistentObjectAsync"/> applies
/// everything a load applies — the row gate, the per-row redaction, breadcrumbs, the fresh etag
/// (from the request session, which wrote it) — and then the page-load presentation: disabled
/// actions, and the static attribute rights (Read-denied removed, Edit-denied read-only).
/// </para>
/// <para>
/// When the caller may create or edit but not read the row (no <c>Read/T</c>, or a row rule that hides
/// the saved row), the answer is the saved object with its Read-denied attributes removed: its id and
/// fresh etag, and only values the caller posted — the shield dropped the rest — and nothing read
/// from the stored document.
/// </para>
/// </remarks>
internal interface ISaveResponsePresenter
{
    Task<PersistentObject> PresentAsync(EntityTypeDefinition definition, PersistentObject saved, bool isNew, CancellationToken cancellationToken = default);
}

[Register(typeof(ISaveResponsePresenter), ServiceLifetime.Scoped)]
internal sealed partial class SaveResponsePresenter : ISaveResponsePresenter
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly IAttributeRightsEnforcement attributeRights;

    public async Task<PersistentObject> PresentAsync(EntityTypeDefinition definition, PersistentObject saved, bool isNew, CancellationToken cancellationToken = default)
    {
        PersistentObject? reloaded = null;
        if (!string.IsNullOrEmpty(saved.Id))
        {
            try
            {
                reloaded = await databaseAccess.GetPersistentObjectAsync(definition.Id, saved.Id);
            }
            catch (SparkAccessDeniedException)
            {
                // May save, may not read: fall through to the posted object, below.
            }
        }

        if (reloaded is null)
        {
            await attributeRights.PresentAsync([saved], SparkCoreActions.Read, isNew ? SparkCoreActions.New : SparkCoreActions.Edit, cancellationToken);
            return saved;
        }

        await disabledActions.ApplyOnLoadAsync(reloaded);
        await attributeRights.PresentAsync([reloaded], SparkCoreActions.Read, SparkCoreActions.Edit, cancellationToken);
        return reloaded;
    }
}
