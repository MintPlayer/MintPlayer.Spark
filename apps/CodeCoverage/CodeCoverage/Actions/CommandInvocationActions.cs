using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// The bot commands on the repository page (#660): who wrote which command, how it ended, and a link to its log. A command
/// is visible iff its repository is, like a backport attempt, and <c>security.json</c> grants only <c>QueryRead</c>: the
/// command lifecycle writes them, never Spark.
/// </summary>
public partial class CommandInvocationActions : DefaultPersistentObjectActions<CommandInvocation>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<CommandInvocation, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return c => c.Repository.In(repoIds);
    }

    /// <summary>
    /// Custom query: a repository's commands, newest first. Source: "Custom.Repository_CommandInvocations". Streamed by the
    /// repository's id prefix, so it builds no index (D-O22's rule for this app).
    /// </summary>
    public async Task<IQueryable<CommandInvocation>> Repository_CommandInvocations(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");
        if (await session.LoadAsync<Repository>(args.Parent!.Id) is not { } repository) return Enumerable.Empty<CommandInvocation>().AsQueryable();

        var commands = new List<CommandInvocation>();
        await using (var stream = await session.Advanced.StreamAsync<CommandInvocation>(CommandInvocation.ForRepository(repository.Provider, repository.GitHubId)))
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Document is { } command && command.Repository == repository.Id) commands.Add(command);
        }
        return commands.OrderByDescending(c => c.ReceivedAtUtc).AsQueryable();
    }
}
