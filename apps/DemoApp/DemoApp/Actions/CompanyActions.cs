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

public partial class CompanyActions : DefaultPersistentObjectActions<Company>, ISparkOwnsRowSecurity, IAfterSave<Company>
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Companies here are sample records with no owner and no per-caller meaning.";

    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IDocumentStore documentStore;

    public async ValueTask OnAfterSaveAsync(Company entity, SaveContext context)
    {
        // Find all employees of this company and broadcast a batch notification message
        using var session = documentStore.OpenAsyncSession();
        var employeeIds = await session.Query<VPerson, People_Overview>()
            .Where(p => p.Company == entity.Id)
            .Select(p => p.Id)
            .ToListAsync();

        if (employeeIds.Count > 0)
        {
            await messageBus.BroadcastAsync(
                new CompanyUpdatedMessage(entity.Id!, entity.Name, employeeIds!));
        }
    }
}
