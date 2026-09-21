using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;
using Microsoft.Extensions.Logging;

namespace CodeCoverage.Services;

/// <summary>
/// The GitHub implementation of <see cref="IForgeClient"/>: resolves the App installation for a
/// repository, then delegates to the GitHub-specific diff and content services.
/// </summary>
/// <remarks>
/// <para>
/// The installation lookup used to live at every call site — five copies of "if the repository has
/// an account, load it and read its InstallationId". It lives here now, once, and is memoized per
/// request because the feedback pipeline asks for access and then immediately performs two or three
/// reads against the same repository.
/// </para>
/// <para>
/// A null installation is not an error. Public repositories are readable through
/// <c>raw.githubusercontent.com</c> with no credential at all, which is why the read methods still
/// pass a nullable installation id down rather than refusing early — only callers that need to
/// *write* (the feedback pipeline) treat its absence as terminal, and they ask
/// <see cref="CheckAccessAsync"/> first.
/// </para>
/// </remarks>
[Register(typeof(IForgeClient), ServiceLifetime.Scoped)]
public partial class GitHubForgeClient : IForgeClient
{
    [Inject] private readonly IGitHubDiffService diffService;
    [Inject] private readonly IGitHubContentService contentService;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService installationService;
    [Inject] private readonly ILogger<GitHubForgeClient> logger;

    /// <summary>Per-request memo, keyed by repository document id. Null value means "resolved, and there is none".</summary>
    private readonly Dictionary<string, long?> installations = [];

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        return installationId is null
            ? ForgeAccess.No("No GitHub App installation for this repository.")
            : ForgeAccess.Yes;
    }

    public async Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default)
        => await diffService.CompareAsync(
            repository, await ResolveInstallationAsync(repository, cancellationToken), baseRef, headSha, cancellationToken);

    public async Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => await diffService.GetFirstParentAsync(
            repository, await ResolveInstallationAsync(repository, cancellationToken), sha, cancellationToken);

    public async Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default)
        => await contentService.GetFileContentAsync(
            repository, await ResolveInstallationAsync(repository, cancellationToken), sha, path, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately requires an installation rather than falling back to the App JWT, although the
    /// JWT can read a public repository's pull requests. The fork-upload design requires the app
    /// installed on the target repository, and an implicit anonymous fallback here would quietly
    /// undo that: the endpoint would start answering for repositories whose owner never installed
    /// anything, which is the consent the installation represents.
    /// </remarks>
    public async Task<ForgePullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        if (installationId is null)
        {
            logger.LogDebug("No installation for {FullName}; cannot read pull request #{Number}.",
                repository.FullName, number);
            return null;
        }

        try
        {
            var client = await installationService.CreateInstallationClientAsync(installationId.Value);
            var pull = await client.PullRequest.Get(repository.OwnerLogin, repository.Name, number);

            return new ForgePullRequest(
                Number: pull.Number,
                HeadSha: pull.Head.Sha,
                // GitHub reports the head branch bare here, unlike the `ref` on a push payload.
                HeadRef: pull.Head.Ref,
                // ⚠️ Null when the fork was deleted after the pull request was opened. Carried
                // through as null rather than coalesced to the base id, because `IsFromFork` must
                // read "cannot tell" as "fork" and coalescing would make it read the opposite.
                HeadRepositoryId: pull.Head.Repository?.Id,
                BaseRef: pull.Base.Ref,
                BaseRepositoryId: pull.Base.Repository.Id,
                BaseRepositoryDefaultBranch: pull.Base.Repository.DefaultBranch,
                IsOpen: pull.State.Value == Octokit.ItemState.Open);
        }
        catch (Octokit.NotFoundException)
        {
            // A number that does not exist, or a repository this installation can no longer see.
            // Both are "no" to the caller, and neither should be distinguishable from outside.
            return null;
        }
        catch (Exception ex)
        {
            // Rate limited, GitHub down, credential broken. ⚠️ Not knowing must not read as a pass:
            // the fork-upload path treats null as a refusal, which is why this returns null rather
            // than rethrowing into a 500 that would invite a retry loop against GitHub.
            logger.LogWarning(ex, "Could not read pull request #{Number} on {FullName}.",
                number, repository.FullName);
            return null;
        }
    }

    /// <summary>
    /// The repository's owning account's installation id, or null when the repository has no
    /// account or the account has no installation.
    /// </summary>
    /// <remarks>
    /// Memoized including the null result, so a repository with no installation does not re-load
    /// the account document on every read within a request.
    /// </remarks>
    private async Task<long?> ResolveInstallationAsync(Repository repository, CancellationToken cancellationToken)
    {
        if (repository.Id is not null && installations.TryGetValue(repository.Id, out var memoized))
            return memoized;

        long? installationId = null;
        if (repository.Account is not null)
            installationId = (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;

        if (repository.Id is not null)
            installations[repository.Id] = installationId;

        return installationId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The three outcomes are logged apart on purpose. Collapsing them is how a permanently dead
    /// feature looks healthy: a missing <c>contents: write</c> is not a transient fault, and
    /// "already gone" is not a failure at all.
    /// </remarks>
    public async Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
    {
        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        if (installationId is null)
        {
            logger.LogWarning("No installation for {FullName}; cannot delete branch {Branch}.",
                repository.FullName, branch);
            return;
        }

        try
        {
            var client = await installationService.CreateInstallationClientAsync(installationId.Value);
            await client.Git.Reference.Delete(repository.OwnerLogin, repository.Name, $"heads/{branch}");
            logger.LogInformation("Deleted branch {Owner}/{Repo}:{Branch}.",
                repository.OwnerLogin, repository.Name, branch);
        }
        catch (Octokit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Almost always a `contents` permission raised after the installation was created and
            // never accepted — in which case the feature is inert everywhere, not just here, and
            // still looks enabled in the UI.
            //
            // Caught on StatusCode rather than ForbiddenException because Octokit raises a bare
            // ApiException for some 403s.
            logger.LogError(ex,
                "Not permitted to delete branch {Owner}/{Repo}:{Branch}. The GitHub App installation "
                + "is missing `contents: write` — a raised permission must be accepted per "
                + "installation before branch deletion can work.",
                repository.OwnerLogin, repository.Name, branch);
        }
        catch (Octokit.NotFoundException)
        {
            // Usually a race with GitHub's own delete_branch_on_merge, or a hand deletion — the
            // intended state is reached either way.
            //
            // ⚠️ But not always: GitHub answers 404 rather than 403 for some refs a token may not
            // write, so this arm can also be a permission failure wearing the wrong status. The
            // message says both, because logging "already gone" at Information for a permission
            // problem is how a dead feature looks healthy.
            logger.LogInformation(
                "Branch {Owner}/{Repo}:{Branch} was already gone, or the installation may not write it.",
                repository.OwnerLogin, repository.Name, branch);
        }
        catch (Exception ex)
        {
            // Never throws: this runs after the merge has landed, and failing the caller would cost
            // the event and change nothing about the merge.
            logger.LogWarning(ex, "Failed to delete branch {Owner}/{Repo}:{Branch}.",
                repository.OwnerLogin, repository.Name, branch);
        }
    }
}
