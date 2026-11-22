using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// What happens to files once a save or delete committed, as after-save and after-delete interceptors, so a failure here is
/// logged and never fails the committed write:
/// <list type="bullet">
/// <item>A store outside the session (<see cref="ISessionFileStore"/> is not implemented) deletes the claimed uploads' staged
/// bytes and the files the saved entity no longer holds; a hard delete or purge removes every object of the entity. What a
/// failure leaves behind, the reconcile (<see cref="FilesReconciler"/>) removes.</item>
/// <item><see cref="IFileUploadHandler.OnFileAcceptedAsync"/> runs for each file the save claimed (#700 §6).</item>
/// </list>
/// </summary>
internal sealed partial class FileStoreInterceptor : IAfterSave, IAfterDelete
{
    [Inject] private readonly FileClaimLedger ledger;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IEnumerable<IFileUploadHandler> handlers;
    [Inject] private readonly ILogger<FileStoreInterceptor> logger;

    /// <summary>Only types with a stored-file property; the answer is cached per type.</summary>
    public bool AppliesTo(Type entityType)
        => entityType.GetProperties().Any(p => FileUploadService.StoredFileType(p) is not null);

    public async ValueTask OnAfterSaveAsync(SaveContext context)
    {
        foreach (var key in ledger.TakeDeletes(context.Entity))
            await DeleteAsync(key);

        foreach (var file in ledger.TakeAccepted(context.Entity))
        {
            foreach (var handler in handlers)
                await handler.OnFileAcceptedAsync(new FileAcceptedContext(context.Entity, file.Attribute, file.File, file.UserId), CancellationToken.None);
        }
    }

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        // A session store's files went with the document; a soft delete keeps the row, and so its files.
        if (fileStore is ISessionFileStore || context.IsReplaced)
            return;
        var keys = new List<string>();
        try
        {
            await foreach (var item in fileStore.ListAsync(fileStore.EntityPrefix(context.Id), CancellationToken.None))
                keys.Add(item.Key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Listing the files of deleted {EntityId} failed; the reconcile removes them.", context.Id);
            return;
        }
        foreach (var key in keys)
            await DeleteAsync(key);
    }

    private async Task DeleteAsync(string key)
    {
        try
        {
            await fileStore.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting stored file {Key} failed; the reconcile removes it.", key);
        }
    }
}
