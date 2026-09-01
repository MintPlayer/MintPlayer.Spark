using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Recipients;

/// <summary>
/// Simple logging recipient for CompanyUpdatedMessage.
/// Demonstrates per-handler retry isolation: if NotifyEmployeesRecipient fails,
/// this handler is NOT re-executed because its success was already recorded.
/// </summary>
public partial class LogCompanyUpdated : IRecipient<CompanyUpdatedMessage>
{
    [Inject] private readonly ILogger<LogCompanyUpdated> _logger;

    public Task HandleAsync(CompanyUpdatedMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Company updated: {CompanyName} ({CompanyId}), {EmployeeCount} employees affected",
            message.CompanyName, message.CompanyId, message.EmployeeIds.Count);
        return Task.CompletedTask;
    }
}
