using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.GraphQL;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.Issues;

namespace WebhooksDemo.Recipients;

public partial class LogIssues : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly ILogger<LogIssues> _logger;
    [Inject] private readonly IGitHubInstallationService _gitHubInstallationService;

    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken cancellationToken)
    {
        try
        {
            var issue = message.Event.Issue;
            _logger.LogInformation(
                "Issue #{Number} ({Action}): {Title} in {Repo}",
                issue.Number, message.Event.Action, issue.Title, message.RepositoryFullName);

            if (message.Event.Action == IssuesActionValue.Opened)
            {
                var githubClient = await _gitHubInstallationService.CreateInstallationClientAsync(message.InstallationId);
                await githubClient.Issue.Comment.Create(
                    message.Event.Repository!.Id, (int)issue.Number, "Thanks for creating this issue");

                var graphql = await _gitHubInstallationService.CreateGraphQLConnectionAsync(message.InstallationId, EClientType.Installation);

                // GraphQL with an installation token has no `viewer` root — resolve the
                // installation's owner via the REST API using the App JWT instead.
                var appClient = await _gitHubInstallationService.CreateAppClientAsync();
                var installation = await appClient.GitHubApps.GetInstallation(message.InstallationId);
                var ownerLogin = installation.Account.Login;
                var isOrganization = installation.TargetType.StringValue == "Organization";

                var projectIds = isOrganization
                    ? await graphql.Run(new Query()
                        .Organization(ownerLogin)
                        .ProjectsV2(null, null, null, null, null, null)
                        .AllPages()
                        .Select(p => p.Id))
                    : await graphql.Run(new Query()
                        .User(ownerLogin)
                        .ProjectsV2(null, null, null, null, null, null)
                        .AllPages()
                        .Select(p => p.Id));
            }
        }
        catch (Exception ex)
        {
            throw;
        }
    }
}
