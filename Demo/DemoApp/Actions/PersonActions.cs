using DemoApp.Library.Entities;
using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    [Inject] private readonly IMessageBus messageBus;

    public override Task OnBeforeSaveAsync(Person entity)
    {
        if (!string.IsNullOrEmpty(entity.Email))
        {
            entity.Email = entity.Email.Trim().ToLowerInvariant();
        }

        entity.FirstName = entity.FirstName?.Trim() ?? string.Empty;
        entity.LastName = entity.LastName?.Trim() ?? string.Empty;

        return Task.CompletedTask;
    }

    public override async Task OnAfterSaveAsync(Person entity)
    {
        Console.WriteLine($"[PersonActions] Person saved: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        await messageBus.BroadcastAsync(new PersonCreatedMessage(entity.Id!, $"{entity.FirstName} {entity.LastName}"));
    }

    public override async Task OnBeforeDeleteAsync(Person entity)
    {
        Console.WriteLine($"[PersonActions] Person being deleted: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        await messageBus.BroadcastAsync(new PersonDeletedMessage(entity.Id!));
    }
}
