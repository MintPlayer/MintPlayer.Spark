using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;

namespace WebhooksDemo.Recipients;

/// <summary>
/// Bot-wide policy: when a pull request closes (merged or not), delete its head
/// branch via the GitHub API. Mirrors the per-repo "Automatically delete head
/// branches" setting at organization scope, so installing the app makes it the
/// default for every repository it sees webhooks from.
/// </summary>
public partial class DeleteBranchOnPullRequestClose : IRecipient<GitHubWebhookMessage<PullRequestEvent>>
{
    [Inject] private readonly IGitHubInstallationService _installationService;
    [Inject] private readonly ILogger<DeleteBranchOnPullRequestClose> _logger;

    public async Task HandleAsync(GitHubWebhookMessage<PullRequestEvent> message, CancellationToken cancellationToken = default)
    {
        if (message.Event.Action != PullRequestActionValue.Closed)
            return;

        var pr = message.Event.PullRequest;
        var headRef = pr.Head.Ref;
        var headRepo = pr.Head.Repo;
        var baseRepo = pr.Base.Repo;

        // Cross-repo PR (fork): the head branch lives in a repo we don't control.
        if (headRepo is null || baseRepo is null || headRepo.Id != baseRepo.Id)
        {
            _logger.LogDebug("Skipping branch delete for PR #{Number}: head is on a different repo (fork).", pr.Number);
            return;
        }

        var owner = baseRepo.Owner.Login;
        var name = baseRepo.Name;

        try
        {
            var client = await _installationService.CreateInstallationClientAsync(message.InstallationId);
            await client.Git.Reference.Delete(owner, name, $"heads/{headRef}");
            _logger.LogInformation("Deleted branch {Owner}/{Repo}:{Ref} after PR #{Number} closed.",
                owner, name, headRef, pr.Number);
        }
        catch (NotFoundException)
        {
            // Race with GitHub's own auto-delete (delete_branch_on_merge), or the branch
            // was already removed manually. Either way, our intended state is reached.
            _logger.LogInformation("Branch {Owner}/{Repo}:{Ref} was already gone for PR #{Number}.",
                owner, name, headRef, pr.Number);
        }
        catch (ApiValidationException ex)
        {
            // 422 typically means the branch is protected — skip rather than retry.
            _logger.LogWarning(ex, "Refused to delete branch {Owner}/{Repo}:{Ref} for PR #{Number} (likely protected).",
                owner, name, headRef, pr.Number);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete branch {Owner}/{Repo}:{Ref} for PR #{Number}.",
                owner, name, headRef, pr.Number);
        }
    }
}
