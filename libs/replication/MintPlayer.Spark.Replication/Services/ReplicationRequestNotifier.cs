using System.Net.Mail;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Models;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Tells the <c>replication:approvers</c> slot that a replication request waits, by default and the same way write-back
/// outcomes reach their initiator (<see cref="SyncOutcomeNotifier"/>):
/// <list type="bullet">
/// <item>the durable record is the pending <see cref="SparkReplicationRequest"/> itself, which the slot's
/// "Replication requests" page lists first (and the live collection refresh updates);</item>
/// <item>each member of the slot gets a private <c>replication:requests</c> frame on their open pages;</item>
/// <item>each member with a confirmed address gets a mail, when MailManager is registered;</item>
/// <item>every <see cref="IReplicationRequestObserver"/> hears it (an app's extra channel).</item>
/// </list>
/// The members are the users holding a group the slot binds to (<see cref="ISparkGroupMembers"/>, from the
/// Authorization package); without it only the collection refresh and the observers run.
/// </summary>
/// <remarks>Never throws: a delivery that fails is logged, the request stands.</remarks>
internal interface IReplicationRequestNotifier
{
    Task NotifyArrivedAsync(IReadOnlyList<SparkReplicationRequest> arrived, CancellationToken cancellationToken);
}

internal sealed partial class ReplicationRequestNotifier : IReplicationRequestNotifier
{
    /// <summary>The private live topic an approver's open pages follow.</summary>
    public const string RequestsTopic = "replication:requests";

    /// <summary>The arrival mail's template (embedded, <c>Mail/SparkReplication/ReplicationRequested.mjml</c>).</summary>
    public const string RequestedTemplate = "SparkReplication/ReplicationRequested";

    /// <summary>The approvers' page, app-relative.</summary>
    public const string RequestsPage = "/query/replication-requests";

    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ILogger<ReplicationRequestNotifier> logger;

    public async Task NotifyArrivedAsync(IReadOnlyList<SparkReplicationRequest> arrived, CancellationToken cancellationToken)
    {
        if (arrived.Count == 0)
            return;

        var approvers = await ApproversAsync(cancellationToken);
        var observers = services.GetServices<IReplicationRequestObserver>().ToList();
        foreach (var request in arrived)
        {
            logger.LogInformation("Replication request {RequestId} from '{Consumer}' for '{Target}' waits for {Slot} ({Count} members).",
                request.Id, request.ConsumerModule, request.TargetCollection, ReplicationRequestArrived.ApproversSlot, approvers.Count);

            Live(request, approvers);
            await MailAsync(request, approvers, cancellationToken);

            var news = new ReplicationRequestArrived(request.Id!, request.ConsumerModule, request.SourceCollection, request.TargetCollection,
                request.Hash, request.ApprovedHash, request.Diff);
            foreach (var observer in observers)
            {
                try { await observer.OnReplicationRequestArrivedAsync(news, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Replication request observer {Observer} failed for {RequestId}.", observer.GetType().Name, request.Id);
                }
            }
        }
    }

    /// <summary>The slot's members: the users holding a group <c>security.json</c> binds <c>replication:approvers</c> to.</summary>
    private async Task<IReadOnlyList<SparkGroupMember>> ApproversAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (services.GetService<ISparkGroupMembers>() is not { } members
                || services.GetService<ISecurityConfigurationLoader>()?.GetConfiguration() is not { } security)
            {
                logger.LogDebug("No group directory: replication requests reach the requests page and the observers only.");
                return [];
            }

            var names = SparkGroupNames.Of(security, ReplicationRequestArrived.ApproversSlot, out var problem);
            if (names.Count == 0)
            {
                logger.LogWarning("The {Slot} slot names no group ({Problem}); nobody is told about replication requests.",
                    ReplicationRequestArrived.ApproversSlot, problem ?? "no binding");
                return [];
            }
            return await members.GetMembersAsync(names, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The members of {Slot} could not be read.", ReplicationRequestArrived.ApproversSlot);
            return [];
        }
    }

    private void Live(SparkReplicationRequest request, IReadOnlyList<SparkGroupMember> approvers)
    {
        try
        {
            if (services.GetService<ISparkLiveTopics>() is not { } topics)
                return;
            var frame = new ReplicationRequestFrame(request.Id!, request.ConsumerModule, request.TargetCollection, request.Status);
            foreach (var approver in approvers)
                topics.PublishPrivate("user:" + approver.UserId, RequestsTopic, frame);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No live notice for replication request {RequestId}.", request.Id);
        }
    }

    private async Task MailAsync(SparkReplicationRequest request, IReadOnlyList<SparkGroupMember> approvers, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
            return;

        foreach (var approver in approvers)
        {
            if (!approver.EmailConfirmed || approver.Email is not { Length: > 0 } email || !MailAddress.TryCreate(email, out _))
                continue;
            try
            {
                await mailer.SendAsync(new SparkMailRequest
                {
                    Template = RequestedTemplate,
                    To = email,
                    ToName = approver.UserName,
                    Culture = approver.PreferredCulture,
                    Data = new
                    {
                        consumer = request.ConsumerModule,
                        source = request.SourceCollection,
                        collection = request.TargetCollection,
                        changes = request.Diff,
                        module = options.Value.ModuleName,
                        page = RequestsPage,
                    },
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The arrival mail of replication request {RequestId} was not sent to {UserId}.", request.Id, approver.UserId);
            }
        }
    }
}

/// <summary>What <c>replication:requests</c> carries: a small DTO, never a persistent object (live payloads skip row security).</summary>
public sealed record ReplicationRequestFrame(string RequestId, string ConsumerModule, string TargetCollection, string Status);
