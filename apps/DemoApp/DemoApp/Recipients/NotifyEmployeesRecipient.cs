using DemoApp.Library.Messages;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Recipients;

/// <summary>
/// Demonstrates ICheckpointRecipient: when a company is updated, notifies each employee.
/// If processing fails partway through, the checkpoint allows retry to resume
/// from the last successfully processed employee instead of re-notifying everyone.
/// </summary>
public partial class NotifyEmployeesRecipient : ICheckpointRecipient<CompanyUpdatedMessage>
{
    [Inject] private readonly ILogger<NotifyEmployeesRecipient> _logger;
    [Inject] private readonly IMessageCheckpoint _checkpoint;

    public Task HandleAsync(CompanyUpdatedMessage message, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: 0, cancellationToken);

    public Task HandleAsync(CompanyUpdatedMessage message, string checkpoint, CancellationToken cancellationToken)
        => ProcessFromIndex(message, startIndex: int.Parse(checkpoint), cancellationToken);

    private async Task ProcessFromIndex(CompanyUpdatedMessage message, int startIndex, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Notifying employees for company {CompanyName} ({CompanyId}), starting from index {StartIndex} of {Total}",
            message.CompanyName, message.CompanyId, startIndex, message.EmployeeIds.Count);

        for (var i = startIndex; i < message.EmployeeIds.Count; i++)
        {
            // Simulate sending a notification (e.g., email, push notification)
            _logger.LogInformation(
                "Notified employee {EmployeeId} about update to {CompanyName}",
                message.EmployeeIds[i], message.CompanyName);

            // Save checkpoint after each successful notification.
            // If the next iteration fails, retry resumes from i+1.
            await _checkpoint.SaveAsync((i + 1).ToString(), cancellationToken);
        }

        _logger.LogInformation(
            "All {Count} employees notified for company {CompanyName}",
            message.EmployeeIds.Count, message.CompanyName);
    }
}
