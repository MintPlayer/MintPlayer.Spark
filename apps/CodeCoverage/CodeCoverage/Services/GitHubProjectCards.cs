using System.Text.RegularExpressions;
using CodeCoverage.Entities;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.GraphQL;
using Octokit.GraphQL.Core;
using Octokit.GraphQL.Model;
using Connection = Octokit.GraphQL.Connection;

namespace CodeCoverage.Services;

/// <summary>Where an issue or pull request ended up on a board.</summary>
public enum ECardOutcome
{
    /// <summary>The card was moved to the requested column.</summary>
    Moved,

    /// <summary>The card was not on the board and was added, then moved.</summary>
    Added,

    /// <summary>The card was not on the board and adding was not requested, so nothing happened.</summary>
    NotOnBoard,
}

/// <summary>
/// Moving issue and pull-request cards between a board's Status columns.
/// </summary>
public interface IGitHubProjectCards
{
    /// <summary>Moves an issue's card to <paramref name="columnOptionId"/>, adding it to the board first if needed.</summary>
    Task<ECardOutcome> MoveIssueAsync(
        GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId, bool addIfMissing, CancellationToken cancellationToken = default);

    /// <summary>Moves a pull request's card to <paramref name="columnOptionId"/>, adding it to the board first if needed.</summary>
    Task<ECardOutcome> MovePullRequestAsync(
        GitHubProject project, string owner, string repo, int pullRequestNumber, string columnOptionId, bool addIfMissing, CancellationToken cancellationToken = default);

    /// <summary>
    /// The issues a pull request closes, via GitHub's <c>closingIssuesReferences</c>. Used so
    /// merging a PR can move the cards of the issues it resolves, not only its own.
    /// </summary>
    Task<IReadOnlyList<(string Repo, int Number)>> GetClosingIssuesAsync(
        long installationId, string owner, string repo, int pullRequestNumber, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubProjectCards"/>
[Register(typeof(IGitHubProjectCards), ServiceLifetime.Scoped)]
public partial class GitHubProjectCards : IGitHubProjectCards
{
    [Inject] private readonly IGitHubInstallationService installations;
    [Inject] private readonly ILogger<GitHubProjectCards> logger;

    public async Task<ECardOutcome> MoveIssueAsync(
        GitHubProject project, string owner, string repo, int issueNumber, string columnOptionId, bool addIfMissing, CancellationToken cancellationToken = default)
    {
        var graphQL = await ConnectAsync(project.InstallationId);

        var itemId = await FindIssueItemAsync(graphQL, owner, repo, issueNumber, project.NodeId);
        var outcome = ECardOutcome.Moved;

        if (itemId is null)
        {
            if (!addIfMissing) return ECardOutcome.NotOnBoard;
            itemId = await AddItemAsync(graphQL, project.NodeId, await IssueNodeIdAsync(graphQL, owner, repo, issueNumber));
            outcome = ECardOutcome.Added;
        }

        await MoveAsync(graphQL, project, itemId.Value, columnOptionId);
        return outcome;
    }

    public async Task<ECardOutcome> MovePullRequestAsync(
        GitHubProject project, string owner, string repo, int pullRequestNumber, string columnOptionId, bool addIfMissing, CancellationToken cancellationToken = default)
    {
        var graphQL = await ConnectAsync(project.InstallationId);

        var itemId = await FindPullRequestItemAsync(graphQL, owner, repo, pullRequestNumber, project.NodeId);
        var outcome = ECardOutcome.Moved;

        if (itemId is null)
        {
            if (!addIfMissing) return ECardOutcome.NotOnBoard;
            itemId = await AddItemAsync(graphQL, project.NodeId, await PullRequestNodeIdAsync(graphQL, owner, repo, pullRequestNumber));
            outcome = ECardOutcome.Added;
        }

        await MoveAsync(graphQL, project, itemId.Value, columnOptionId);
        return outcome;
    }

    public async Task<IReadOnlyList<(string Repo, int Number)>> GetClosingIssuesAsync(
        long installationId, string owner, string repo, int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        var graphQL = await ConnectAsync(installationId);

        var results = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .PullRequest(pullRequestNumber)
                .ClosingIssuesReferences()
                .AllPages()
                .Select(issue => new { issue.Number, RepoName = issue.Repository.Name }));

        return results.Select(r => (r.RepoName, r.Number)).ToList();
    }

    /// <summary>
    /// Sets the item's Status field to the requested option.
    /// <para>
    /// Refuses when the board has no cached <see cref="GitHubProject.StatusFieldId"/>, rather than
    /// sending an empty field id and letting GitHub reject it. A board can legitimately have no
    /// Status field, and that is a configuration problem the caller must report against the rule —
    /// not a transient GraphQL error to retry.
    /// </para>
    /// </summary>
    private async Task MoveAsync(Connection graphQL, GitHubProject project, ID itemId, string columnOptionId)
    {
        if (string.IsNullOrEmpty(project.StatusFieldId))
        {
            throw new InvalidOperationException(
                $"Board #{project.Number} ({project.Name}) has no cached Status field, so a card cannot be moved. "
                + "Run Sync columns on the board, or check that it still has a Status field.");
        }

        await RunCleanedUpAsync(graphQL,
            new Mutation()
                .UpdateProjectV2ItemFieldValue(new UpdateProjectV2ItemFieldValueInput
                {
                    ClientMutationId = Guid.NewGuid().ToString(),
                    ProjectId = new ID(project.NodeId),
                    ItemId = itemId,
                    FieldId = new ID(project.StatusFieldId),
                    Value = new() { SingleSelectOptionId = columnOptionId },
                })
                .Select(r => r.ClientMutationId));
    }

    private async Task<ID> IssueNodeIdAsync(Connection graphQL, string owner, string repo, int number)
        => await graphQL.Run(new Query().Repository(owner: owner, name: repo).Issue(number).Select(i => i.Id));

    private async Task<ID> PullRequestNodeIdAsync(Connection graphQL, string owner, string repo, int number)
        => await graphQL.Run(new Query().Repository(owner: owner, name: repo).PullRequest(number).Select(pr => pr.Id));

    private async Task<ID> AddItemAsync(Connection graphQL, string projectNodeId, ID contentNodeId)
        => await graphQL.Run(
            new Mutation()
                .AddProjectV2ItemById(new AddProjectV2ItemByIdInput
                {
                    ClientMutationId = Guid.NewGuid().ToString(),
                    ProjectId = new ID(projectNodeId),
                    ContentId = contentNodeId,
                })
                .Select(r => r.Item.Id));

    private async Task<ID?> FindIssueItemAsync(Connection graphQL, string owner, string repo, int number, string projectNodeId)
    {
        var items = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .Issue(number)
                .ProjectItems()
                .AllPages()
                .Select(x => new { ProjectId = x.Project.Id, ItemId = x.Id }));

        return items.FirstOrDefault(it => it.ProjectId.Value == projectNodeId)?.ItemId;
    }

    private async Task<ID?> FindPullRequestItemAsync(Connection graphQL, string owner, string repo, int number, string projectNodeId)
    {
        var items = await graphQL.Run(
            new Query()
                .Repository(owner: owner, name: repo)
                .PullRequest(number)
                .ProjectItems()
                .AllPages()
                .Select(x => new { ProjectId = x.Project.Id, ItemId = x.Id }));

        return items.FirstOrDefault(it => it.ProjectId.Value == projectNodeId)?.ItemId;
    }

    private Task<Connection> ConnectAsync(long installationId)
        => installations.CreateGraphQLConnectionAsync(installationId, EClientType.Installation);

    /// <summary>
    /// Runs a mutation with null field values stripped from the serialized document.
    /// <para>
    /// <b>Ugly and load-bearing, migrated deliberately unchanged.</b>
    /// <c>UpdateProjectV2ItemFieldValueInput.Value</c> is a union — text, number, date,
    /// singleSelectOptionId, iterationId — and the typed builder emits every member, with the four
    /// we are not setting as <c>null</c>. GitHub then answers with an error for those nulls even
    /// though the mutation itself succeeds, so <c>Connection.Run</c>'s typed path throws on a write
    /// that worked. Stripping the nulls from the document before sending is what avoids that.
    /// </para>
    /// <para>
    /// The alternative — accepting the error and treating it as success — would mean discarding
    /// every real error on this mutation too. Removing this is only safe once the builder stops
    /// emitting unset union members, or GitHub stops complaining about them.
    /// </para>
    /// <para>
    /// The <c>catch (Exception) { throw; }</c> that wrapped this in the original is gone: it added
    /// a stack frame and nothing else.
    /// </para>
    /// </summary>
    private static async Task<string> RunCleanedUpAsync<T>(Connection connection, IQueryableValue<T> expression)
    {
        var expr = expression.Compile().ToString() ?? string.Empty;

        expr = NullValueRegex().Replace(expr, match =>
            match.Groups["c1"]?.Value == "," && match.Groups["c2"]?.Value == "," ? "," : " ");
        expr = NewlineRegex().Replace(expr, " ");

        var query = System.Text.Json.JsonSerializer.Serialize(new { query = expr });
        return await connection.Run(query);
    }

    [GeneratedRegex(@"(?<c1>\,?)(text|number|date|singleSelectOptionId|iterationId):\s?null(?<c2>\,?)\s?")]
    private static partial Regex NullValueRegex();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex NewlineRegex();
}
