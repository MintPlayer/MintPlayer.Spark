using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Links;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

// The shareable URLs (/{provider}/a/{login}, /{provider}/r/{owner}/{repo}, …/c/{sha}) through Spark's redirect hook
// (#602, tier c): a server render answers them with a 301 to the object's page before Angular runs, where the client
// guards in vanity-redirects.ts take a round trip. Spark loads the named object through the read path, so a private
// repository or a missing one is the same uniform refusal: these lookups need no authorization of their own.
// The client guards stay for in-app navigation and for as long as the app renders in the browser only.

/// <summary><c>/{provider}/a/{login}</c>: the account, matched on the owner key (never the login alone).</summary>
internal sealed partial class AccountVanityRedirect : ISparkEntityRedirect
{
    [Inject] private readonly IAsyncDocumentSession session;

    public string Pattern => "{provider}/a/{login}";

    public async Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!ForgeProviders.TryParse(values["provider"], out var forge))
            return null;
        var ownerKey = new ForgeOwner(forge, values["login"]).ToString();
        var account = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.OwnerKey == ownerKey)
            .FirstOrDefaultAsync(cancellationToken);
        return account?.Id is { } id ? new SparkEntityTarget("account", id) : null;
    }
}

/// <summary><c>/{provider}/r/{owner}/{repo}</c>: the repository, as <see cref="IRepositoryScope"/> resolves it for this caller.</summary>
internal sealed partial class RepositoryVanityRedirect : ISparkEntityRedirect
{
    [Inject] private readonly IRepositoryScope scope;

    public string Pattern => "{provider}/r/{owner}/{repo}";

    public async Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        => await scope.ResolveAsync(values["provider"], values["owner"], values["repo"], cancellationToken) is { Id: { } id }
            ? new SparkEntityTarget("repository", id)
            : null;
}

/// <summary><c>/{provider}/r/{owner}/{repo}/c/{sha}</c>: the commit of a repository this caller may see.</summary>
internal sealed partial class CommitVanityRedirect : ISparkEntityRedirect
{
    [Inject] private readonly IRepositoryScope scope;

    public string Pattern => "{provider}/r/{owner}/{repo}/c/{sha}";

    public async Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(values["provider"], values["owner"], values["repo"], cancellationToken);
        return repository is null
            ? null
            : new SparkEntityTarget("commit", Commit.DocumentId(repository.Provider, repository.GitHubId, values["sha"]));
    }
}
