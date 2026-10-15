using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// Supplies the options of the merge queue's exclusion lists: the checks that have reported at least once (merge-queue
/// PRD §11, O38, D110–D112).
/// <para>
/// The same mechanism as <see cref="ProjectColumnActions"/>: <c>[Reference(typeof(ForgeCheck), "...")]</c> on a
/// <c>List&lt;string&gt;</c> plus a <c>Custom.*</c> query that reads <see cref="CustomQueryArgs.Parent"/>. The rows are
/// <see cref="Repository.KnownChecks"/>, embedded in the repository document and returned in memory; each row's id is
/// the check's name, which is exactly what an exclusion list stores, so the picker writes the name with no translation
/// and <c>CheckGate</c> reads it unchanged.
/// </para>
/// </summary>
public partial class ForgeCheckActions : DefaultPersistentObjectActions<ForgeCheck>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkVisibility visibility;

    /// <summary>The repositories whose checks the account-level list offers; an account has far fewer.</summary>
    private const int MaxAccountRepositories = 1024;

    /// <inheritdoc />
    /// <remarks>
    /// <c>ForgeCheck</c> has no Raven collection, so there is no queryable for a row filter to narrow, and
    /// <c>GetRowFilterAsync</c> carries no parent to scope by in any case (the same reasoning as
    /// <see cref="ProjectColumnActions.RowSecurityRationale"/>).
    /// </remarks>
    public string RowSecurityRationale =>
        "Rows are never served from a collection: the only queries, Repository_ForgeChecks and Account_ForgeChecks below, " +
        "return the KnownChecks embedded in the parent repository, or in the repositories of the parent account, and " +
        "return empty unless CustomQueryArgs.Parent is present, is of the matching type, and the caller manages its owner " +
        "(ISparkVisibility.CanManageOwnerAsync). The endpoint has already resolved the parent through a Read-checked load " +
        "(Endpoints/Queries/Execute.cs, DatabaseAccess.GetPersistentObjectAsync), so a parent the caller may not see is a " +
        "404 before this runs; the manager gate narrows further, because check names of a private repository are only " +
        "for those who may change its merge-queue settings. The raw session loads and the account query below bypass row " +
        "security and are safe only for that reason: they read the owner the gate has just authorized, and a manager sees " +
        "every private repository of that owner anyway (RepositoryVisibility). Every failure path narrows to an empty list.";

    /// <summary>The checks that have reported on the parent repository: the options of its own exclusion list.</summary>
    /// <remarks>
    /// Empty rather than <c>args.EnsureParent(...)</c>, and empty for a caller who does not manage the owner, never a
    /// 403 or 404 from inside the query (D112): the picker then shows no options instead of an error.
    /// </remarks>
    public async Task<IEnumerable<ForgeCheck>> Repository_ForgeChecks(CustomQueryArgs args)
    {
        if (args.Parent is null
            || !string.Equals(args.ParentType, nameof(Repository), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var repository = await session.LoadAsync<Repository>(args.Parent.Id);
        if (repository is null || !await visibility.CanManageOwnerAsync(repository.OwnerKey))
            return [];

        return repository.KnownChecks ?? [];
    }

    /// <summary>
    /// The checks that have reported on any repository of the parent account: the options of the account's exclusion
    /// list, which applies to all of them.
    /// </summary>
    /// <remarks>
    /// One row per name, the most recent sighting's. A manager of the account already sees every private repository of
    /// it, so the union shows nothing new (D112); the account's list itself stays publicly readable, which D113 accepts.
    /// </remarks>
    public async Task<IEnumerable<ForgeCheck>> Account_ForgeChecks(CustomQueryArgs args)
    {
        if (args.Parent is null
            || !string.Equals(args.ParentType, nameof(Account), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var account = await session.LoadAsync<Account>(args.Parent.Id);
        if (account is null || !await visibility.CanManageOwnerAsync(account.OwnerKey))
            return [];

        var accountId = account.Id;
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == accountId)
            .Take(MaxAccountRepositories)
            .ToListAsync();

        return [.. repositories
            .SelectMany(r => r.KnownChecks ?? [])
            .Where(check => !string.IsNullOrEmpty(check?.Id))
            .GroupBy(check => check.Id, StringComparer.Ordinal)
            .Select(same => same.MaxBy(check => check.LastSeenUtc)!)];
    }
}
