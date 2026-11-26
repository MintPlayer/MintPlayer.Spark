using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Files.Storage;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// Removes an account's <see cref="Account.CustomAvatar"/>, for the Administrators (#700 M11, PRD Q15: "only the owner uploads;
/// admins can clear"). The account falls back to the forge's avatar.
/// </summary>
/// <remarks>
/// <para>
/// A custom action rather than Edit: an administrator manages no account, so <c>AccountActions</c> withholds Edit from them
/// (D-A4), and widening Edit for one attribute would hand them every setting. <c>ClearAvatar/Account</c> is granted to the
/// Administrators alone in <c>security.json</c>, and the action touches nothing but the avatar.
/// </para>
/// <para>
/// A clear is moderation, so the picture is <b>erased</b>, not only detached: <see cref="IFileStore.EraseAsync"/> deletes the
/// original, its variants and, on the RavenDB store, the revisions still holding it. It runs after the commit, so a refused save
/// leaves the account pointing at bytes that still exist; an erase that fails after the commit leaves unreferenced bytes that the
/// orphan reconcile removes, never a broken avatar.
/// </para>
/// </remarks>
public partial class ClearAvatarAction : SparkCustomAction
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IManager manager;
    [Inject] private readonly ILogger<ClearAvatarAction> logger;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        // The parent must be an account: a load by id alone would read any document as an Account.
        var id = args.Parent is { ObjectTypeId: var type } parent && type == AccountAvatar.AccountType ? parent.Id : null;
        if (string.IsNullOrEmpty(id) || await session.LoadAsync<Account>(id, cancellationToken) is not { } account)
        {
            manager.Client.Notify("That account no longer exists.", NotificationKind.Warning);
            return;
        }

        if (account.CustomAvatar is not { } avatar)
        {
            manager.Client.Notify($"'{account.Login}' has no custom avatar.", NotificationKind.Info);
            return;
        }

        account.CustomAvatar = null;
        await session.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(avatar.FileId))
        {
            try
            {
                await fileStore.EraseAsync(account.Id.Value, avatar.FileId, cancellationToken);
            }
            catch (Exception ex)
            {
                // The account no longer references the file, so nothing serves it; the reconcile collects the bytes.
                logger.LogWarning(ex, "Clearing the avatar of {Account}: erasing file {FileId} failed", account.Id, avatar.FileId);
            }
        }

        manager.Client.Notify($"Removed the custom avatar of '{account.Login}'.", NotificationKind.Success);
    }
}
