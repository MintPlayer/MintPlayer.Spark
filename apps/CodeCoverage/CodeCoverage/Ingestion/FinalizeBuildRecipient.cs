using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

public partial class FinalizeBuildRecipient : IRecipient<FinalizeBuildMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubDiffService diffService;
    [Inject] private readonly ILogger<FinalizeBuildRecipient> logger;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(FinalizeBuildMessage message, CancellationToken cancellationToken = default)
    {
        var build = await session.LoadAsync<Build>(message.BuildId, cancellationToken);
        if (build is null)
        {
            logger.LogWarning("Build {BuildId} not found — skipping finalize", message.BuildId);
            return;
        }

        await BuildFinalizer.Finalize(session, diffService, build, "Explicit", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        await messageBus.BroadcastAsync(new Feedback.PublishFeedbackMessage { BuildId = message.BuildId }, cancellationToken);
        logger.LogInformation("Finalized build {BuildId} (Explicit)", message.BuildId);
    }
}
