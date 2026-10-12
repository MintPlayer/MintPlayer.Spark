using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// Which repositories and branches the caller may see: the rule the browse API applies, in one place, so the
/// MCP tools (MCP PRD §4.8.2, D9) read through exactly the same gate as the website.
/// </summary>
/// <remarks>
/// <para>
/// Public repositories are world-readable; a private one needs the viewer's forge-verified access to its owner
/// (<see cref="RepositoryVisibility"/>). <b>Resolving one</b> repository follows renames and transfers through
/// <see cref="IRepositoryResolver"/>; <b>enumerating</b> follows the narrower listing rule, which stops advertising a
/// repository the app lost access to (see <see cref="RepositoryVisibility.ListingFilter"/>).
/// </para>
/// <para>
/// ⚠️ <b>A repository the caller may not see, a missing one and one on a forge this deployment does not have all
/// answer <see langword="null"/>, by the same steps (#453).</b> The existence of a private repository is itself
/// private information of the organisation that installed the app. Each surface turns <see langword="null"/> into
/// its own single refusal (browse: <c>SparkDenial.Refuse</c>, MCP: <c>SparkMcpResult.Refuse</c>) and must never
/// branch on why.
/// </para>
/// <para>
/// Extracted from <c>BrowseController</c> (plan M6) without changing what it answers: the owner set is still
/// fetched before resolving and unconditionally, and an unrecognised forge is still a refusal, never a default.
/// </para>
/// </remarks>
public interface IRepositoryScope
{
    /// <summary>
    /// Every owner the caller may see private repositories of, as <c>provider:login</c> keys; empty for an anonymous
    /// caller. Asked of the forges once per request.
    /// </summary>
    Task<string[]> GetAllowedOwnerKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The repository the URL names, when the caller may see it; <see langword="null"/> otherwise, whatever the reason.
    /// </summary>
    /// <param name="provider">The forge as the URL spells it (<c>github</c>). An unrecognised one resolves to nothing.</param>
    Task<Repository?> ResolveAsync(string provider, string owner, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repositories an account's page lists for the caller, by the listing rule; <see langword="null"/> for a forge
    /// this deployment does not have.
    /// </summary>
    Task<AccountRepositories?> ListAccountAsync(string provider, string login, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repositories listed to the caller across every account (the listing rule), ordered by full name, optionally
    /// narrowed to full names containing <paramref name="search"/>. At most <see cref="MaxListed"/> are considered.
    /// </summary>
    Task<IReadOnlyList<Repository>> ListAsync(string? search, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repository's own commits: never a fork's. Fork-contributed commits carry this repository's id, so without
    /// this they would read as its history, under a branch name the fork chose; they stay reachable through their pull
    /// request, the only context in which they mean anything.
    /// </summary>
    IRavenQueryable<Indexes.VCommit> Commits(Repository repository);

    /// <summary>Distinct branches of the repository's own commits that have coverage, the default branch first.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(Repository repository, CancellationToken cancellationToken = default);
}

/// <summary>An account's repositories as its page lists them for one caller.</summary>
/// <param name="OwnerKey">The account's <c>provider:login</c> key.</param>
/// <param name="CallerSeesPrivate">Whether the caller may see this owner's private repositories (and so manage them).</param>
public sealed record AccountRepositories(string OwnerKey, bool CallerSeesPrivate, IReadOnlyList<Repository> Repositories);

/// <inheritdoc cref="IRepositoryScope"/>
[Register(typeof(IRepositoryScope), ServiceLifetime.Scoped)]
public partial class RepositoryScope : IRepositoryScope
{
    /// <summary>How many repositories one listing considers: the bound an account page has always had.</summary>
    public const int MaxListed = 1024;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IRepositoryResolver repositories;
    [Inject] private readonly IForgeIntegrationResolver forges;

    // Task-memoized, so the owner set costs one forge fan-out per request however often it is asked.
    private Task<string[]>? owners;

    public Task<string[]> GetAllowedOwnerKeysAsync(CancellationToken cancellationToken = default)
        => owners ??= forges.GetAllowedOwnerKeysAsync(cancellationToken);

    public async Task<Repository?> ResolveAsync(string provider, string owner, string name, CancellationToken cancellationToken = default)
    {
        // An unrecognised forge resolves to nothing rather than throwing: every caller already treats null as the
        // canonical refusal, the right answer for a URL naming a forge this deployment does not have. Answering it with
        // another forge's data would be exactly the silent defaulting the multi-forge work removed.
        if (!ForgeProviders.TryParse(provider, out var forge))
            return null;

        // ⚠️ The owner set is fetched BEFORE resolving and unconditionally (#453). Fetching it only once a private
        // repository turned up made the private path alone pay a (possibly cold) forge call, and the visibility is the
        // resolver's, so an invisible repository takes the same steps as a missing one rather than being found and
        // then discarded.
        var allowed = await GetAllowedOwnerKeysAsync(cancellationToken);
        return (await repositories.ResolveAsync(
            forge, owner, name, r => RepositoryVisibility.IsVisible(r, allowed), cancellationToken)).Repository;
    }

    public async Task<AccountRepositories?> ListAccountAsync(string provider, string login, CancellationToken cancellationToken = default)
    {
        // The owner set first, as the account endpoints always did: an unknown forge then costs what a known one does.
        var allowed = await GetAllowedOwnerKeysAsync(cancellationToken);
        if (!ForgeProviders.TryParse(provider, out var forge))
            return null;

        var ownerKey = new ForgeOwner(forge, login).ToString();
        var listed = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerKey == ownerKey)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);

        return new AccountRepositories(
            ownerKey,
            allowed.Contains(ownerKey, StringComparer.OrdinalIgnoreCase),
            [.. listed.Where(r => RepositoryVisibility.IsListed(r, allowed))]);
    }

    public async Task<IReadOnlyList<Repository>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        var allowed = await GetAllowedOwnerKeysAsync(cancellationToken);

        // The listing rule in the query (it is RavenDB-translatable for exactly this), and again in memory: the two
        // forms are kept equal by RepositoryVisibility's parity test, and the second pass costs nothing.
        var listed = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(RepositoryVisibility.ListingFilter(allowed))
            .Take(MaxListed)
            .ToListAsync(cancellationToken);

        return [.. listed
            .Where(r => RepositoryVisibility.IsListed(r, allowed))
            .Where(r => string.IsNullOrWhiteSpace(search) || r.FullName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Provider)
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)];
    }

    public IRavenQueryable<Indexes.VCommit> Commits(Repository repository)
        => session.Query<Indexes.VCommit, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.ContributedFromFork != true);

    public async Task<IReadOnlyList<string>> ListBranchesAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        // A fork's head branch must not appear in this repository's branch list: it is a branch name from somebody
        // else's repository, and picking it would render an empty badge.
        var branches = await Commits(repository)
            .Where(c => c.HasCoverage)
            .Select(c => c.Branch)
            .Distinct()
            .Take(200)
            .ToListAsync(cancellationToken);

        return [.. branches
            .Where(b => !string.IsNullOrEmpty(b))
            .OrderByDescending(b => b == repository.DefaultBranch)
            .ThenBy(b => b, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()];
    }
}
