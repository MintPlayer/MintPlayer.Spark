using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;

namespace CodeCoverage.Services;

/// <summary>One repository as an installation reports it.</summary>
public sealed record InstallationRepository(
    long GitHubId,
    string Name,
    string FullName,
    string OwnerLogin,
    bool IsPrivate,
    string? DefaultBranch,
    bool Archived);

/// <summary>
/// What a GitHub App installation can currently see.
/// <para>
/// A seam, not a layer: it exists so the reconciler's decisions — which are irreversible in one
/// direction and site-wide in blast radius — can be tested against an absent repository, a 404 and
/// a transient failure without a live GitHub or a mocked <c>IGitHubClient</c> graph.
/// </para>
/// </summary>
public interface IInstallationRepositories
{
    /// <summary>
    /// Every repository the installation can see. Throws — <see cref="NotFoundException"/> and
    /// <see cref="AuthorizationException"/> mean the installation is gone; anything else means
    /// GitHub could not answer, which is emphatically not the same thing.
    /// </summary>
    Task<IReadOnlyList<InstallationRepository>> ListAsync(long installationId, int max, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInstallationRepositories"/>
[Register(typeof(IInstallationRepositories), ServiceLifetime.Scoped)]
public partial class InstallationRepositories : IInstallationRepositories
{
    [Inject] private readonly IGitHubInstallationService installations;

    /// <summary>GitHub's maximum page size, so a large installation costs as few calls as possible.</summary>
    private const int PageSize = 100;

    public async Task<IReadOnlyList<InstallationRepository>> ListAsync(
        long installationId, int max, CancellationToken cancellationToken = default)
    {
        var client = await installations.CreateInstallationClientAsync(installationId);

        // Paged explicitly. A truncated list is the one failure mode that must not happen here:
        // the reconciler reads "absent from this list" as "we lost access", so a missing page
        // would disconnect a whole organization at once.
        var all = new List<InstallationRepository>();
        for (var page = 1; ; page++)
        {
            var options = new ApiOptions { PageSize = PageSize, PageCount = 1, StartPage = page };
            var response = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent(options);
            if (response.Repositories.Count == 0) break;

            foreach (var repository in response.Repositories)
            {
                all.Add(new InstallationRepository(
                    repository.Id,
                    repository.Name,
                    repository.FullName,
                    repository.Owner?.Login ?? repository.FullName.Split('/')[0],
                    repository.Private,
                    repository.DefaultBranch,
                    repository.Archived));
            }

            if (response.Repositories.Count < PageSize) break;
            if (all.Count >= response.TotalCount) break;
            if (all.Count >= max) break;
        }

        return all;
    }
}
