using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Runs the assembler for one commit after a build finalized, then publishes
/// feedback for that build against the assembled headline. Serialized by the
/// queue, so two builds of one commit finalizing back to back assemble twice in
/// order — the second run sees both and is the one that sticks.
/// </summary>
public partial class AssembleCommitRecipient : IRecipient<AssembleCommitMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ICommitAssembler assembler;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<AssembleCommitRecipient> logger;

    public async Task HandleAsync(AssembleCommitMessage message, CancellationToken cancellationToken = default)
    {
        var assembly = await assembler.AssembleAsync(message.CommitId, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (assembly is null)
            logger.LogWarning("Commit {CommitId} has no finalized build — nothing assembled", message.CommitId);
        else
            logger.LogInformation("Assembled {CommitId}: {Measured} measured + {Carried} carried, {Completeness} ({Reasons})",
                message.CommitId, assembly.MeasuredFiles, assembly.CarriedFiles, assembly.Completeness, string.Join(",", assembly.IncompleteReasons));

        if (message.BuildId is not null)
            await messageBus.BroadcastAsync(new Feedback.PublishFeedbackMessage { BuildId = message.BuildId }, cancellationToken);
    }
}
