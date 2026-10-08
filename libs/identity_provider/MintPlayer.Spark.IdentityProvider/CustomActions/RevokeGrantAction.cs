using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.CustomActions;

/// <summary>An identity-provider administrator revokes users' grants (D9): the tokens issued under them die with them.</summary>
internal sealed partial class RevokeGrantAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcGrantWithdrawal withdrawal;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (!await access.IsAdministratorAsync()) return;
        foreach (var id in args.SelectedItems.Select(i => i.Id))
        {
            if (await session.LoadAsync<OidcGrant>(id, cancellationToken) is { } grant)
                await withdrawal.WithdrawAsync(grant.Subject, grant.ApplicationId, scopes: null, access.IpAddress, cancellationToken);
        }
        manager.Client.RefreshQuery("oidc-grants");
    }
}
