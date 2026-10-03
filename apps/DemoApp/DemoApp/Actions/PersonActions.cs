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
    IBeforeSave<Person>, IAfterSave<Person>, IAfterDelete<Person>
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

        return ValueTask.CompletedTask;
    }

    public async ValueTask OnAfterSaveAsync(Person entity, SaveContext context)
    {
        Console.WriteLine($"[PersonActions] Person saved: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        await messageBus.BroadcastAsync(new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));
    }

    // After the commit (#482): broadcasting before it announced deletes that a refusal then undid.
    public async ValueTask OnAfterDeleteAsync(Person entity, DeleteContext context)
    {
        Console.WriteLine($"[PersonActions] Person deleted: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        await messageBus.BroadcastAsync(new PersonDeletedMessage(entity.Id!));
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
