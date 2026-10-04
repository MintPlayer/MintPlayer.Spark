using CodeCoverage.Entities;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// The application's side of <see cref="IGitHubIndexQueries"/>: the GitHub integration's queries,
/// written against the indexes generated here, which is the only project that has them (#388).
/// Stateless; every call runs on the caller's session.
/// </summary>
[Register(typeof(IGitHubIndexQueries), ServiceLifetime.Singleton)]
public sealed class GitHubIndexQueries : IGitHubIndexQueries
{
    public Task<List<Repository>> RepositoriesNamedAsync(IAsyncDocumentSession session, string fullName, int take, CancellationToken cancellationToken)
        => session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == fullName)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<List<Repository>> RepositoriesPreviouslyNamedAsync(IAsyncDocumentSession session, string fullName, int take, CancellationToken cancellationToken)
        => session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.PreviousFullNames.Any(previous => previous == fullName))
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<bool> AccountExistsAsync(IAsyncDocumentSession session, string ownerKey, CancellationToken cancellationToken)
        => session.Query<Account, Indexes.Accounts_Overview>()
            .AnyAsync(a => a.OwnerKey == ownerKey, cancellationToken);

    public Task<List<Repository>> RepositoriesOfAccountAsync(IAsyncDocumentSession session, string accountId, int take, CancellationToken cancellationToken)
        => session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == accountId)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<List<GitHubProject>> ProjectsOfAccountAsync(IAsyncDocumentSession session, string accountId, int take, CancellationToken cancellationToken)
        => session.Query<GitHubProject, Indexes.GitHubProjects_Overview>()
            .Where(p => p.Account == accountId)
            .Take(take)
            .ToListAsync(cancellationToken);
}
