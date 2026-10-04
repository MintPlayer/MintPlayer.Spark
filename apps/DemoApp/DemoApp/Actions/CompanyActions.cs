using MintPlayer.Spark.Abstractions.Authorization;
using DemoApp.Indexes;
using DemoApp.Library.Entities;
using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace DemoApp.Actions;

public partial class CompanyActions : DefaultPersistentObjectActions<Company>, ISparkOwnsRowSecurity, IAfterSaveCommitted<Company>
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Companies here are sample records with no owner and no per-caller meaning.";

    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IDocumentStore documentStore;

    // Durable (#482, D17): delivered by Messaging after the commit, with retries. It reads the company
    // as it is now — the payload carries no entity.
    public async Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        // Find all employees of this company and broadcast a batch notification message
        using var session = documentStore.OpenAsyncSession();
        var company = await session.LoadAsync<Company>(change.Id, cancellationToken);
        if (company is null)
            return;

        var employeeIds = await session.Query<VPerson, People_Overview>()
            .Where(p => p.Company == change.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        if (employeeIds.Count > 0)
        {
            await messageBus.BroadcastAsync(
                new CompanyUpdatedMessage(change.Id, company.Name, employeeIds!), cancellationToken);
        }
    }
}
