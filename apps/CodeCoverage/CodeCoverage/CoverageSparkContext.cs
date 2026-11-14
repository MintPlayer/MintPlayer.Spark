using CodeCoverage.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace CodeCoverage;

public partial class CoverageSparkContext : SparkContext
{
    public IQueryable<Account> Accounts => Session.Query<Account>();
    public IQueryable<Repository> Repositories => Session.Query<Repository>();
    public IQueryable<Commit> Commits => Session.Query<Commit>();
    public IQueryable<Build> Builds => Session.Query<Build>();

    /// <summary>
    /// GitHub Projects V2 boards and their automation rules. Registering the property here is what
    /// makes <see cref="GitHubProject"/> a Spark persistent object at all — model synchronization
    /// discovers types through this context, so an entity that exists but is not listed here gets no
    /// model, no endpoints and no grid, silently.
    /// </summary>
    public IQueryable<GitHubProject> GitHubProjects => Session.Query<GitHubProject>();

    /// <summary>
    /// Upload tokens, so they are managed through the generic UI rather than a hand-written card.
    /// </summary>
    /// <remarks>
    /// ⚠️ Registering this creates a query endpoint and a per-object endpoint over a collection of
    /// credentials. Two things keep that safe, and both are load-bearing:
    /// <c>ApiTokenActions</c> filters rows to the accounts the caller manages, and
    /// <c>ApiToken.Hash</c> is absent from <c>ApiToken.json</c> so it is never projected onto the
    /// wire. Adding Hash to the model would undo the second one silently.
    /// </remarks>
    public IQueryable<ApiToken> ApiTokens => Session.Query<ApiToken>();

    // The commit-graph mirrors (#504, M5b): the administrators' list, with each mirror's size.
    public IQueryable<RepositoryGraphState> RepositoryGraphStates => Session.Query<RepositoryGraphState>();

    // The versioning history (#153 M12): read-only, listed per repository by Custom.Repository_VersioningRuns.
    public IQueryable<VersioningRun> VersioningRuns => Session.Query<VersioningRun>();
    // What became of each backport (release lines recovery plan, segment B): read-only, listed per repository by
    // Custom.Repository_BackportAttempts.
    public IQueryable<BackportAttempt> BackportAttempts => Session.Query<BackportAttempt>();
    // The bot commands written in comments (#660): read-only, listed per repository by Custom.Repository_CommandInvocations.
    public IQueryable<CommandInvocation> CommandInvocations => Session.Query<CommandInvocation>();
    // The platform lifecycle (#568 M3): public, read-only, written by PlatformLifecycleCronJob.
    public IQueryable<PlatformRelease> PlatformReleases => Session.Query<PlatformRelease>();
}
