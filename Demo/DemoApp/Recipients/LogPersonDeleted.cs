using DemoApp.Library.Messages;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Recipients;

public class LogPersonDeleted : IRecipient<PersonDeletedMessage>
{
    private readonly ILogger<LogPersonDeleted> _logger;

    public LogPersonDeleted(ILogger<LogPersonDeleted> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(PersonDeletedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person deleted: {PersonId}", message.PersonId);
        return Task.CompletedTask;
    }
}
