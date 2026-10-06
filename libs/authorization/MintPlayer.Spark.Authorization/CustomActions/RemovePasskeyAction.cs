using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Authorization.Actions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.CustomActions;

/// <summary>
/// Removes the selected passkey, unless it is the account's last way in (PRD D4).
/// </summary>
/// <remarks>
/// The confirmation is the action's <c>confirmation</c> text in the library's <c>actions.json</c>
/// (<c>actions.RemovePasskey.confirmation</c>), asked by the grid before the request is sent. The old
/// page removed without asking. The last-credential guard is <c>SparkCredentialInventory</c>, as on
/// the endpoint it replaces; its refusal is a notification, so the row simply stays.
/// </remarks>
internal sealed partial class RemovePasskeyAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPasskeyAccount passkeys;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.SelectedItems is not [{ Id: { Length: > 0 } id }])
            return;

        switch (await passkeys.RemoveAsync(id))
        {
            case SparkPasskeyOutcome.Done:
                manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyRemovedNotice"), NotificationKind.Success);
                manager.Client.RefreshQuery(SparkPasskeysPage.QueryAlias);
                break;
            case SparkPasskeyOutcome.LastCredential:
                manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyLastCredential"), NotificationKind.Error);
                break;
            default:
                manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyFailed"), NotificationKind.Error);
                break;
        }
    }
}
