using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Presents the persistent object a retry prompt carries (<c>IRetryAccessor.Action(…, persistentObject)</c>)
/// the way every other read path presents one (contributions M2c-2b, PRD §5 "Custom-action results"):
/// static attribute rights first — Read-denied attributes removed, Edit-denied read-only — then, when
/// the object is a stored row of an entity type, the per-row <c>GetProtectedAttributesAsync("Read")</c>
/// blanking.
/// </summary>
/// <remarks>
/// A hook building a prompt may hand over a loaded object (already redacted by the load, so this
/// only re-applies the same answer) or one it filled itself — that second case is why the prompt is
/// presented at all. A transient prompt type with no stored row gets the static half only.
/// </remarks>
internal static class RetryPresentation
{
    public static async Task PresentAsync(IServiceProvider services, PersistentObject prompt, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IAttributeRightsEnforcement>()
            .PresentAsync([prompt], SparkCoreActions.Read, SparkCoreActions.Edit, cancellationToken);

        if (string.IsNullOrEmpty(prompt.Id)
            || services.GetRequiredService<IModelLoader>().GetEntityType(prompt.ObjectTypeId) is not { } definition
            || SparkTypeResolver.ResolveClrType(definition.ClrType) is not { } clrType)
        {
            return;
        }

        var rowSecurity = services.GetRequiredService<IRowSecurity>();
        if (!rowSecurity.HasProtectedAttributesHook(clrType))
            return;

        var session = services.GetRequiredService<IAsyncDocumentSession>();
        var documents = await RowSecurity.LoadBaseDocumentsAsync(session, clrType, [prompt.Id!], cancellationToken);
        if (documents.TryGetValue(prompt.Id!, out var document))
            await rowSecurity.RedactAsync(session, [(prompt, document)], clrType, clrType, SparkCoreActions.Read, cancellationToken);
    }
}
