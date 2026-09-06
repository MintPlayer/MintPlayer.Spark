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
    [Inject] private readonly ILogger<GitHubStateReconciler> logger;

    /// <summary>Bound on one account's repository set; the session's request budget is 30.</summary>
    private const int MaxRepositoriesPerAccount = 1024;

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
            logger.LogInformation("Installation {InstallationId} for {Login} is gone; disconnecting its repositories",
                installationId, account.Login);
            account.InstallationId = null;
            foreach (var repository in await LoadRepositoriesOfAsync(account, cancellationToken))
                Disconnect(repository, DisconnectedReasons.AppUninstalled);
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
}
