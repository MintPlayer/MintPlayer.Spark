using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <inheritdoc cref="ReconcileAccountMessage"/>
public partial class ReconcileAccountRecipient : IRecipient<ReconcileAccountMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubStateReconciler reconciler;
    [Inject] private readonly ILogger<ReconcileAccountRecipient> logger;

    public async Task HandleAsync(ReconcileAccountMessage message, CancellationToken cancellationToken = default)
    {
        var account = await session.LoadAsync<Account>(Account.DocumentId(message.AccountGitHubId), cancellationToken);
        if (account is null) return;

        // No installation means there is nothing to ask. The account's repositories were already
        // disconnected by whatever removed the installation.
        if (account.InstallationId is null) return;

        await reconciler.ReconcileAsync(account, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reconciled {Login} after an installation change", account.Login);
    }
}
