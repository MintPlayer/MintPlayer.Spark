using CodeCoverage.Entities;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// The index queries the GitHub integration needs, implemented by the application.
/// </summary>
/// <remarks>
/// <c>[GenerateIndex]</c> emits index classes only in the application project (#388): an index is
/// deployed from there and has no business in a class library. This library therefore cannot name
/// <c>Indexes.Repositories_Overview</c> and friends, and the application, which references this
/// library and owns the indexes, implements this port. Each call runs on the caller's
/// <paramref name="session"/>, so the entities it returns are tracked by that session and saved with it.
/// </remarks>
public interface IGitHubIndexQueries
{
    /// <summary>Repositories whose current <c>owner/name</c> is <paramref name="fullName"/>, on any forge.</summary>
    Task<List<Repository>> RepositoriesNamedAsync(IAsyncDocumentSession session, string fullName, int take, CancellationToken cancellationToken);

    /// <summary>Repositories that used to be known as <paramref name="fullName"/>, on any forge.</summary>
    Task<List<Repository>> RepositoriesPreviouslyNamedAsync(IAsyncDocumentSession session, string fullName, int take, CancellationToken cancellationToken);

    /// <summary>Whether an account with this owner key exists.</summary>
    Task<bool> AccountExistsAsync(IAsyncDocumentSession session, string ownerKey, CancellationToken cancellationToken);

    /// <summary>The repositories of one account.</summary>
    Task<List<Repository>> RepositoriesOfAccountAsync(IAsyncDocumentSession session, string accountId, int take, CancellationToken cancellationToken);

    /// <summary>The project boards of one account.</summary>
    Task<List<GitHubProject>> ProjectsOfAccountAsync(IAsyncDocumentSession session, string accountId, int take, CancellationToken cancellationToken);
}
