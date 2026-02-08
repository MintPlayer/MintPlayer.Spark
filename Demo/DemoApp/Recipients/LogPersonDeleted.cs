using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Recipients;

public partial class LogPersonDeleted : IRecipient<PersonDeletedMessage>
{
    [Inject] private readonly ILogger<LogPersonDeleted> _logger;

    public Task HandleAsync(PersonDeletedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person deleted: {PersonId}", message.PersonId);
        return Task.CompletedTask;
    }
}
