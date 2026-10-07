using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.Actions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.CustomActions;

/// <summary>
/// Renames the selected passkey through a retry form of its own virtual prompt type,
/// <c>PasskeyRename</c> (PRD D4, S3), which replaces the old page's inline editing.
/// </summary>
/// <remarks>
/// The selected row is rebuilt by re-running <c>Custom.MyPasskeys</c>, and the store is asked for the
/// credential among the signed-in user's own, so another user's credential id is simply not found.
/// The name is bounded and cleaned by the same <c>Sanitize</c> the retired endpoints used, whatever
/// the form's own <c>maxLength</c> rule let through.
/// </remarks>
internal sealed partial class RenamePasskeyAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPasskeyAccount passkeys;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.SelectedItems is not [{ Id: { Length: > 0 } id }])
            return;

        // Read-only, so it is safe to repeat on the pass that carries the answer.
        var passkey = await passkeys.FindAsync(id);
        if (passkey is null)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyFailed"), NotificationKind.Error);
            return;
        }

        var prompt = await manager.GetPersistentObjectAsync("PasskeyRename", cancellationToken: cancellationToken);
        prompt["Name"].Value = passkey.Name;

        // Cancel is the client's own, translated button (cancellable), answered as RetryResult.CancelOption.
        var save = manager.GetTranslatedMessage("auth.passkeySave");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("auth.passkeyRename"),
            options: [save],
            defaultOption: save,
            persistentObject: prompt,
            cancellable: true);

        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption)
            return;

        var name = answer.PersistentObject is { } form && form.TryGetAttribute("Name", out var attribute)
            ? attribute.Value?.ToString()
            : null;

        if (await passkeys.RenameAsync(id, name) != SparkPasskeyOutcome.Done)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyFailed"), NotificationKind.Error);
            return;
        }

        manager.Client.RefreshQuery(SparkPasskeysPage.QueryAlias);
    }
}
