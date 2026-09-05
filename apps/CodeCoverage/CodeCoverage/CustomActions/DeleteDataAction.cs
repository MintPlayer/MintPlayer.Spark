using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// Deletes a disconnected repository and every document beneath it.
/// </summary>
/// <remarks>
/// The right <c>DeleteData/Repository</c> is granted to every signed-in user, because rights in
/// this app are group-level and there is no group per GitHub owner — so the right decides who is
/// <em>offered</em> the action, and this method decides who may actually run it. Both checks matter:
/// the caller must manage the owner, and the repository must be disconnected. A connected
/// repository is never deletable here, which is what keeps "we lost access to it" and "destroy the
/// data" two separate decisions made by two different parties.
/// </remarks>
public partial class DeleteDataAction : SparkCustomAction
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ILogger<DeleteDataAction> logger;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent is not { } parent)
            return;

        var repositoryId = parent.Id;
        if (string.IsNullOrEmpty(repositoryId))
            return;

        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null)
            return;

        if (!await visibility.CanManageOwnerAsync(repository.OwnerLogin))
        {
            logger.LogWarning("Refused DeleteData on {FullName}: caller does not manage {Owner}",
                repository.FullName, repository.OwnerLogin);
            return;
        }

        if (repository.Connection != RepositoryConnection.Disconnected)
        {
            logger.LogWarning("Refused DeleteData on {FullName}: it is still connected", repository.FullName);
            return;
        }

        var principal = httpContextAccessor.HttpContext?.User;
        var user = principal is null ? null : await userManager.GetUserAsync(principal);

        // Broadcast rather than delete inline: the sweep is one document per commit, per build, per
        // covered file and per flag, which is not work to do inside a button press.
        await messageBus.BroadcastAsync(new DeleteRepositoryDataMessage
        {
            RepositoryGitHubId = repository.GitHubId,
            RequestedByUserId = user?.Id ?? "unknown",
        }, cancellationToken);

        logger.LogInformation("Queued deletion of {FullName}", repository.FullName);
    }
}
