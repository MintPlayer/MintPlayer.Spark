using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests;

/// <summary>
/// Resolves <c>owner/name</c> by the live full name only — no aliases, no GitHub.
/// <para>
/// The controller tests care about what a controller does once it has a repository, not about how
/// the name was resolved; that is <c>RepositoryResolverTests</c>'s job. Giving them the real
/// resolver would drag an installation service and a memory cache into five harnesses and let a
/// network call appear in a controller test, so they get the behaviour these tests always assumed:
/// exact name, or nothing.
/// </para>
/// </summary>
public sealed class TestRepositoryResolver(IAsyncDocumentSession? session) : IRepositoryResolver
{
    public async Task<RepositoryResolution> ResolveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        if (session is null) return RepositoryResolution.None;

        var fullName = $"{owner}/{name}";
        var repository = await session.Query<Repository, CodeCoverage.Indexes.Repositories_Overview>()
            .Where(r => r.FullName == fullName)
            .FirstOrDefaultAsync(cancellationToken);

        return repository is null ? RepositoryResolution.None : new RepositoryResolution(repository, false);
    }
}
