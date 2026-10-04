using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// Revokes the selected upload tokens: stamps <see cref="ApiToken.RevokedAtUtc"/> so they stop
/// authenticating, without deleting the documents.
/// </summary>
/// <remarks>
/// ⚠️ <b>A custom action rather than the generic Delete, and that is the point.</b> Revocation is a
/// soft delete, and the two are not interchangeable here: the audit trail of which token existed,
/// who created it and when it stopped working is the useful part of a credential record. So
/// <c>security.json</c> grants <c>QueryReadEditNew/ApiToken</c> — no <c>Delete</c> — and this action
/// is the only way a token stops working.
/// <para>
/// A <b>query</b> action (#467, D22): it sits in the header of the account's upload-token card and
/// acts on the ticked rows (<c>selectionRule "&gt;0"</c>), with a confirmation, since a revoke cannot
/// be undone.
/// </para>
/// <para>
/// The permission check is its own thing. <c>Revoke/ApiToken</c> is granted to every signed-in user,
/// because rights are group-level and there is no group per GitHub owner; the right decides who is
/// <em>offered</em> the button, and the ownership check below decides who may actually use it — per
/// token, because a caller can post ids it was never shown. That is the same split
/// <c>DeleteDataAction</c> documents. One token the caller does not manage refuses the whole request:
/// a selection is revoked entirely or not at all.
/// </para>
/// <para>
/// Every exit says what happened. A silent return is indistinguishable from success in the browser.
/// </para>
/// </remarks>
public partial class RevokeTokenAction : SparkCustomAction
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = args.SelectedItems.Select(row => row.Id).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        if (ids.Length == 0)
        {
            manager.Client.Notify("No token is selected, so there is nothing to revoke.", NotificationKind.Warning);
            return;
        }

        var loaded = await session.LoadAsync<ApiToken>(ids, cancellationToken);
        var tokens = loaded.Values.Where(token => token is not null).ToList();
        if (tokens.Count != ids.Length)
        {
            manager.Client.Notify("One of the selected tokens no longer exists. Nothing was revoked.", NotificationKind.Warning);
            return;
        }

        // ⚠️ Re-checked here, per token, not inherited from the row filter. The filter decides what a
        // caller can see; a write must decide for itself, because a caller can post an id it was never
        // shown. The token names its account by document id; the visibility check takes the account's
        // owner KEY (`github:acme`), read from the account itself.
        var accountIds = tokens.Select(t => t.Account).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        var accounts = await session.LoadAsync<Account>(accountIds!, cancellationToken);
        foreach (var token in tokens)
        {
            var account = token.Account is { Length: > 0 } accountId ? accounts.GetValueOrDefault(accountId) : null;
            if (account is null || !await visibility.CanManageOwnerAsync(account.OwnerKey))
            {
                manager.Client.Notify("You do not manage the account of every selected token. Nothing was revoked.", NotificationKind.Error);
                return;
            }
        }

        var toRevoke = tokens.Where(t => t.RevokedAtUtc is null).ToList();
        if (toRevoke.Count == 0)
        {
            manager.Client.Notify(tokens.Count == 1 ? "That token was already revoked." : "Those tokens were already revoked.", NotificationKind.Info);
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var token in toRevoke)
            token.RevokedAtUtc = now;
        await session.SaveChangesAsync(cancellationToken);

        manager.Client.Notify(
            toRevoke.Count == 1 ? $"Revoked '{toRevoke[0].Description}'." : $"Revoked {toRevoke.Count} tokens.",
            NotificationKind.Success);

        // Refresh the grid the button was pressed from, by ALIAS — RefreshQuery takes the query's
        // alias, not its name, and is silently dropped when that query is not open.
        manager.Client.RefreshQuery("account-upload-tokens");
    }
}
