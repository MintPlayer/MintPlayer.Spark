using MintPlayer.Spark.Abstractions.Authorization;
using DemoApp.Indexes;
using DemoApp.Library.Entities;
using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace DemoApp.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>, ISparkOwnsRowSecurity,
    IBeforeSave<Person>, IAfterSaveCommitted<Person>, IAfterDeleteCommitted<Person>
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose: security.json grants this type to the anonymous role so the sample app works with no sign-in. There is nothing per-caller in a demo Person, so no filter would have anything to narrow. A real application listing people would need one.";

    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IAsyncDocumentSession session;

    public ValueTask OnBeforeSaveAsync(Person entity, SaveContext context)
    {
        if (!string.IsNullOrEmpty(entity.Email))
        {
            entity.Email = entity.Email.Trim().ToLowerInvariant();
        }

        entity.FirstName = entity.FirstName?.Trim() ?? string.Empty;
        entity.LastName = entity.LastName?.Trim() ?? string.Empty;

        // The durable hook below gets a payload, not the entity: what it announces is recorded here.
        context.Facts[NameFact] = $"{entity.FirstName} {entity.LastName}";
        return ValueTask.CompletedTask;
    }

    private const string NameFact = "DemoApp.Name";

    // Durable (#482, D17): written in the save's own commit and delivered by Messaging, so a committed
    // save is always announced and a refused one never is.
    public async Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        var name = change.Facts.GetValueOrDefault(NameFact, string.Empty);
        Console.WriteLine($"[PersonActions] Person saved: {name} (ID: {change.Id})");
        await messageBus.BroadcastAsync(new PersonCreatedMessage(change.Id, name), cancellationToken);
    }

    public async Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[PersonActions] Person deleted (ID: {change.Id})");
        await messageBus.BroadcastAsync(new PersonDeletedMessage(change.Id), cancellationToken);
    }

    /// <summary>
    /// Custom query: returns people belonging to a specific company.
    /// Source: "Custom.Company_People"
    /// </summary>
    public IRavenQueryable<VPerson> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return session.Query<VPerson, People_Overview>()
            .Where(p => p.Company == args.Parent!.Id);
    }
}
