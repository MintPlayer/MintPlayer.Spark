using CodeCoverage.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace CodeCoverage;

public partial class CoverageSparkContext : SparkContext
{
    public IRavenQueryable<Account> Accounts => Session.Query<Account>();
    public IRavenQueryable<Repository> Repositories => Session.Query<Repository>();
    public IRavenQueryable<Commit> Commits => Session.Query<Commit>();
    public IRavenQueryable<Build> Builds => Session.Query<Build>();

    /// <summary>
    /// GitHub Projects V2 boards and their automation rules. Registering the property here is what
    /// makes <see cref="GitHubProject"/> a Spark persistent object at all — model synchronization
    /// discovers types through this context, so an entity that exists but is not listed here gets no
    /// model, no endpoints and no grid, silently.
    /// </summary>
    public IRavenQueryable<GitHubProject> GitHubProjects => Session.Query<GitHubProject>();
}
