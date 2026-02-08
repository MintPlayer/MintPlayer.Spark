using DemoApp.Library.Messages;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Recipients;

public class LogPersonCreated : IRecipient<PersonCreatedMessage>
{
    private readonly ILogger<LogPersonCreated> _logger;

    public LogPersonCreated(ILogger<LogPersonCreated> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(PersonCreatedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Person created: {FullName} ({PersonId})", message.FullName, message.PersonId);
        return Task.CompletedTask;
    }
}
