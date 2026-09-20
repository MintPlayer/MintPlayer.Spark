using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

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
}
