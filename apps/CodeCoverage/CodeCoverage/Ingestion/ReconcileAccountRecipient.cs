using CodeCoverage.Forge;
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
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly ILogger<ReconcileAccountRecipient> logger;

    public async Task HandleAsync(ReconcileAccountMessage message, CancellationToken cancellationToken = default)
    {
        var account = await session.LoadAsync<Account>(
            Account.DocumentId(message.Provider, message.AccountId), cancellationToken);
        if (account is null) return;

        // Disconnected means there is nothing to ask. The account's repositories were already
        // disconnected by whatever revoked our access.
        //
        // ⚠️ Written as `!= Connected` rather than `== Disconnected` would be: every account
        // document written before the field existed has no `Connection` property at all, and an
        // absent field materialises as the enum's default — which is Connected, and correct for
        // them, since an account only existed because access had been granted.
        if (account.Connection != RepositoryConnection.Connected) return;

        await forges.For(account.Provider).ReconcileAsync(account, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reconciled {Login} after an installation change", account.Login);
    }
}
