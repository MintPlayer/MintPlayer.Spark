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
/// Revokes an upload token: stamps <see cref="ApiToken.RevokedAtUtc"/> so it stops authenticating,
/// without deleting the document.
/// </summary>
/// <remarks>
/// ⚠️ <b>A custom action rather than the generic Delete, and that is the point.</b> Revocation is a
/// soft delete, and the two are not interchangeable here: the audit trail of which token existed,
/// who created it and when it stopped working is the useful part of a credential record. So
/// <c>security.json</c> grants <c>QueryReadEditNew/ApiToken</c> — no <c>Delete</c> — and this action
/// is the only way a token stops working.
/// <para>
/// The permission check is its own thing. <c>Revoke/ApiToken</c> is granted to every signed-in user,
/// because rights are group-level and there is no group per GitHub owner; the right decides who is
/// <em>offered</em> the button, and the ownership check below decides who may actually use it. That
/// is the same split <c>DeleteDataAction</c> documents.
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
        if (args.Parent is not { Id.Length: > 0 } page)
        {
            manager.Client.Notify("No token in context, so there is nothing to revoke.", NotificationKind.Warning);
            return;
        }

        var token = await session.LoadAsync<ApiToken>(page.Id, cancellationToken);
        if (token is null)
        {
            manager.Client.Notify("That token no longer exists.", NotificationKind.Warning);
            return;
        }

        // ⚠️ Re-checked here, not inherited from the row filter. The filter decides what a caller
        // can see; a write must decide for itself, because a caller can post an id it was never
        // shown.
        if (token.AccountLogin is null || !await visibility.CanManageOwnerAsync(token.AccountLogin))
        {
            manager.Client.Notify("You do not manage the account this token belongs to.", NotificationKind.Error);
            return;
        }

        if (token.RevokedAtUtc is not null)
        {
            manager.Client.Notify("That token was already revoked.", NotificationKind.Info);
            return;
        }

        token.RevokedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);

        manager.Client.Notify($"Revoked '{token.Description}'.", NotificationKind.Success);

        // Refresh the grid the button was pressed from, by ALIAS — RefreshQuery takes the query's
        // alias, not its name, and is silently dropped when that query is not open.
        manager.Client.RefreshQuery("account-upload-tokens");
    }
}
