using CodeCoverage.Entities;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// Makes our Account and Repository documents agree with what a GitHub App installation can
/// actually see. Called nightly for every installed account, and on demand for the accounts a
/// signed-in caller manages.
/// </summary>
public interface IGitHubStateReconciler
{
    /// <summary>
    /// Reconciles one account. Mutates documents in the caller's session and does <b>not</b> save —
    /// the caller decides the unit of work.
    /// </summary>
    Task ReconcileAsync(Account account, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubStateReconciler"/>
[Register(typeof(IGitHubStateReconciler), ServiceLifetime.Scoped)]
public partial class GitHubStateReconciler : IGitHubStateReconciler
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IInstallationRepositories installationRepositories;
    [Inject] private readonly IInstallationProjects installationProjects;
    [Inject] private readonly ILogger<GitHubStateReconciler> logger;

    /// <summary>Bound on one account's repository set; the session's request budget is 30.</summary>
    private const int MaxRepositoriesPerAccount = 1024;

    /// <summary>
    /// Bound on one account's board set. Far lower than the repository bound because boards are
    /// counted in single digits in practice — the whole MintPlayer organization had one when this
    /// was measured — and because the listing is unpaged, so this is the point at which truncation
    /// would start looking like absence.
    /// </summary>
    private const int MaxProjectsPerAccount = 100;

    public async Task ReconcileAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (account.InstallationId is not { } installationId) return;

        IReadOnlyList<InstallationRepository> live;
        try
        {
            live = await installationRepositories.ListAsync(installationId, MaxRepositoriesPerAccount, cancellationToken);
        }
        catch (Exception ex) when (IsInstallationGone(ex))
        {
            // The installation no longer exists — uninstalled, or the account itself is gone.
            logger.LogInformation("Installation {InstallationId} for {Login} is gone; disconnecting its repositories and boards",
                installationId, account.Login);
            account.InstallationId = null;
            foreach (var repository in await LoadRepositoriesOfAsync(account, cancellationToken))
                Disconnect(repository, DisconnectedReasons.AppUninstalled);
            foreach (var project in await LoadProjectsOfAsync(account, cancellationToken))
                DisconnectProject(project, DisconnectedReasons.AppUninstalled);
            return;
        }

        // Every other failure — a 5xx, a rate limit, a socket, a timeout — means we do not know
        // what the installation holds. Not knowing is emphatically not the same as knowing it holds
        // nothing: treating a transient GitHub outage as absence would disconnect every repository
        // of every account in one sweep and blank the site. So we change nothing and try tomorrow.
        // This is the single most dangerous line in the reconciler; it fails closed on purpose.

        var known = await LoadRepositoriesOfAsync(account, cancellationToken);
        var knownById = known.ToDictionary(r => r.GitHubId);
        var liveIds = new HashSet<long>();

        foreach (var ghRepo in live)
        {
            liveIds.Add(ghRepo.GitHubId);

            if (!knownById.TryGetValue(ghRepo.GitHubId, out var repository))
            {
                // Not necessarily new to us — it may belong to an account we have not associated it
                // with yet, which is what a transfer INTO this installation looks like.
                var id = Repository.DocumentId(ghRepo.GitHubId);
                repository = await session.LoadAsync<Repository>(id, cancellationToken);
                if (repository is null)
                {
                    repository = new Repository { GitHubId = ghRepo.GitHubId };
                    await session.StoreAsync(repository, id, cancellationToken);
                }
            }

            if (!string.IsNullOrEmpty(repository.FullName)
                && repository.FullName != ghRepo.FullName
                && !repository.PreviousFullNames.Contains(repository.FullName, StringComparer.OrdinalIgnoreCase))
            {
                // A rename or transfer we were never told about — the exact drift this job exists
                // to repair. Remember the old name so its published badges keep resolving.
                repository.PreviousFullNames.Add(repository.FullName);
            }

            repository.Account = account.Id;
            repository.Name = ghRepo.Name;
            repository.FullName = ghRepo.FullName;
            repository.OwnerLogin = ghRepo.OwnerLogin;
            repository.IsPrivate = ghRepo.IsPrivate;
            repository.DefaultBranch = ghRepo.DefaultBranch;
            repository.Archived = ghRepo.Archived;
            Connect(repository);
        }

        // Ours, but absent from what the installation returned: the App cannot see it any more.
        // This is the check that finds a repository transferred out of the organization, whether or
        // not any webhook told us about it.
        foreach (var repository in known)
        {
            if (liveIds.Contains(repository.GitHubId)) continue;
            if (repository.Connection == RepositoryConnection.Disconnected) continue;

            logger.LogInformation("{FullName} is no longer visible to installation {InstallationId}; disconnecting",
                repository.FullName, installationId);
            Disconnect(repository, DisconnectedReasons.RemovedFromInstallation);
        }

        await ReconcileProjectsAsync(account, installationId, cancellationToken);
    }

    /// <summary>
    /// Makes our <see cref="GitHubProject"/> documents agree with the boards the installation can
    /// see, on the same terms as repositories: upsert what is reachable, disconnect what is not,
    /// and <b>never delete</b>.
    /// <para>
    /// Never deleting is what makes the automation rules safe to own. The rules live on this
    /// document, so deleting a board that stopped appearing would destroy the user's configuration
    /// on the strength of one GitHub response — and if that response was wrong, there is nothing to
    /// restore from.
    /// </para>
    /// <para>
    /// Board failures are contained rather than propagated: a board listing that fails leaves the
    /// boards untouched and does not abort the repository reconciliation that has already
    /// succeeded, because coverage ingestion depends on repositories and must not be held hostage
    /// to a Projects-V2 outage.
    /// </para>
    /// </summary>
    private async Task ReconcileProjectsAsync(Account account, long installationId, CancellationToken cancellationToken)
    {
        var isOrganization = string.Equals(account.Type, "Organization", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<InstallationProject> live;
        try
        {
            live = await installationProjects.ListAsync(
                installationId, account.Login, isOrganization, MaxProjectsPerAccount, cancellationToken);
        }
        catch (Exception ex) when (IsInstallationGone(ex))
        {
            logger.LogInformation(
                "Installation {InstallationId} cannot see boards for {Login}; disconnecting them",
                installationId, account.Login);
            foreach (var project in await LoadProjectsOfAsync(account, cancellationToken))
                DisconnectProject(project, DisconnectedReasons.AppUninstalled);
            return;
        }
        catch (Exception ex)
        {
            // Same rule as the repository path, and the same reasoning: not knowing what the
            // installation holds is not the same as knowing it holds nothing. Change nothing and
            // try tomorrow. Unlike the repository path this is caught rather than left to
            // propagate, so a Projects-V2 failure cannot undo a repository reconciliation that
            // already worked — the boards are the newer, less critical half.
            logger.LogWarning(ex,
                "Could not list boards for {Login} (installation {InstallationId}); leaving board state unchanged",
                account.Login, installationId);
            return;
        }

        var known = await LoadProjectsOfAsync(account, cancellationToken);
        var knownByNodeId = known.ToDictionary(p => p.NodeId, StringComparer.Ordinal);
        var liveNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var board in live)
        {
            liveNodeIds.Add(board.NodeId);

            if (!knownByNodeId.TryGetValue(board.NodeId, out var project))
            {
                // Keyed on the node id, so this is an idempotent upsert: re-running discovery over
                // a board we already hold updates it instead of minting a duplicate. A board moved
                // between owners keeps its node id, which is why the number is not the key.
                var id = GitHubProject.DocumentId(board.NodeId);
                project = await session.LoadAsync<GitHubProject>(id, cancellationToken);
                if (project is null)
                {
                    project = new GitHubProject { NodeId = board.NodeId };
                    await session.StoreAsync(project, id, cancellationToken);
                }
            }

            project.Account = account.Id;
            project.OwnerLogin = account.Login;
            project.InstallationId = installationId;
            project.Number = board.Number;
            project.Name = board.Title;
            // AutomationEnabled, DeleteBranchOnPrClose and EventMappings are deliberately NOT
            // touched. Discovery owns identity and reachability; the user owns configuration.
            ConnectProject(project);

            // Columns ARE refreshed here, and that is load-bearing rather than convenience.
            //
            // Nothing tells us when a column is renamed, reordered or deleted. The projects_v2 and
            // projects_v2_item webhook events are organization-scoped, so an app installed on a
            // USER account never receives them at all — and even on an organization we do not
            // subscribe to them. So the only ways the cached columns can be corrected are this
            // nightly pass and the manual SyncColumns action. Leaving it to the button alone means
            // a renamed or deleted column leaves every rule pointing at it silently inert, with a
            // configuration screen that still looks right.
            //
            // Only refreshed for boards with automation switched on. A board nobody automates is
            // discovered but inert, and spending a GitHub call per board per night to cache columns
            // for rules that do not exist would make the nightly sweep scale with the number of
            // boards an organization happens to own rather than with the number it uses.
            if (project.AutomationEnabled)
                await RefreshColumnsAsync(project, installationId, cancellationToken);
        }

        foreach (var project in known)
        {
            if (liveNodeIds.Contains(project.NodeId)) continue;
            if (project.Connection == RepositoryConnection.Disconnected) continue;

            logger.LogInformation(
                "Board #{Number} ({Name}) is no longer visible to installation {InstallationId}; disconnecting",
                project.Number, project.Name, installationId);
            DisconnectProject(project, DisconnectedReasons.RemovedFromInstallation);
        }
    }

    /// <summary>
    /// Whether an exception means "this installation is gone" as opposed to "GitHub could not
    /// answer right now". Only the two statuses that assert absence qualify; everything else,
    /// explicitly including rate limits and 5xx, is transient and must leave our state alone.
    /// </summary>
    private static bool IsInstallationGone(Exception ex)
        => ex is NotFoundException
            || (ex is AuthorizationException auth && auth.StatusCode == System.Net.HttpStatusCode.Unauthorized);

    private async Task<IReadOnlyList<Repository>> LoadRepositoriesOfAsync(Account account, CancellationToken ct)
    {
        if (account.Id is null) return [];

        return await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == account.Id)
            .Take(MaxRepositoriesPerAccount)
            .ToListAsync(ct);
    }

    private static void Connect(Repository repository)
    {
        repository.Connection = RepositoryConnection.Connected;
        repository.DisconnectedReason = null;
        repository.DisconnectedAtUtc = null;
    }

    private static void Disconnect(Repository repository, string reason)
    {
        repository.Connection = RepositoryConnection.Disconnected;
        repository.DisconnectedReason = reason;
        repository.DisconnectedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Re-reads one board's Status field and replaces its cached columns.
    /// <para>
    /// Failure here is contained to the board: a column refresh that throws leaves the previously
    /// cached columns in place, which are stale but usable, rather than emptying them. Emptying
    /// them would turn a transient GitHub failure into "every rule on this board points at a
    /// column that does not exist".
    /// </para>
    /// <para>
    /// Rules pointing at options that have genuinely disappeared are <b>not</b> deleted here. The
    /// user owns their rules, and a rule whose column was deleted is information they need to see —
    /// silently dropping it would hide the reason their automation stopped working. The recipient
    /// logs and skips such a rule at dispatch time instead.
    /// </para>
    /// </summary>
    private async Task RefreshColumnsAsync(GitHubProject project, long installationId, CancellationToken cancellationToken)
    {
        try
        {
            var statusField = await installationProjects.GetStatusFieldAsync(installationId, project.NodeId, cancellationToken);

            if (!statusField.Exists)
            {
                // A board with no Status field is a legitimate state, not a failure — but it means
                // automation has nothing to target, so say so rather than leaving stale columns
                // that suggest otherwise.
                logger.LogWarning(
                    "Board #{Number} ({Name}) has no Status field; automation has no column to move cards to",
                    project.Number, project.Name);
                project.StatusFieldId = null;
                project.Columns = [];
                project.ColumnsSyncedAtUtc = DateTime.UtcNow;
                return;
            }

            project.StatusFieldId = statusField.FieldId;
            // Qualified: Octokit has its own ProjectColumn (classic projects), and the using
            // directives here bring both into scope.
            project.Columns = statusField.Columns
                .Select(c => new CodeCoverage.Entities.ProjectColumn { Id = c.OptionId, Name = c.Name })
                .ToList();
            project.ColumnsSyncedAtUtc = DateTime.UtcNow;

            var orphaned = project.EventMappings
                .Where(m => m.Enabled && !string.IsNullOrEmpty(m.TargetColumnOptionId))
                .Where(m => project.Columns.All(c => c.Id != m.TargetColumnOptionId))
                .ToList();

            if (orphaned.Count > 0)
            {
                logger.LogWarning(
                    "Board #{Number} ({Name}) has {Count} enabled rule(s) targeting a column that no longer exists: {Events}",
                    project.Number, project.Name, orphaned.Count,
                    string.Join(", ", orphaned.Select(m => m.EventType)));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not refresh columns for board #{Number} ({Name}); keeping the cached ones",
                project.Number, project.Name);
        }
    }

    private async Task<IReadOnlyList<GitHubProject>> LoadProjectsOfAsync(Account account, CancellationToken ct)
    {
        if (account.Id is null) return [];

        return await session.Query<GitHubProject, Indexes.GitHubProjects_Overview>()
            .Where(p => p.Account == account.Id)
            .Take(MaxProjectsPerAccount)
            .ToListAsync(ct);
    }

    private static void ConnectProject(GitHubProject project)
    {
        project.Connection = RepositoryConnection.Connected;
        project.DisconnectedReason = null;
        project.DisconnectedAtUtc = null;
    }

    private static void DisconnectProject(GitHubProject project, string reason)
    {
        project.Connection = RepositoryConnection.Disconnected;
        project.DisconnectedReason = reason;
        project.DisconnectedAtUtc = DateTime.UtcNow;
    }
}
