using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
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
/// <para>
/// A chip of the list links to <c>/po/forgecheck/&lt;name&gt;</c>; <see cref="OnLoadAsync"/> serves that page from the name
/// alone (D117): the actions class, not a storage flag, decides how an object with no document behind it loads.
/// </para>
/// </summary>
public partial class ForgeCheckActions : DefaultPersistentObjectActions<ForgeCheck>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IEntityMapper entityMapper;

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
        "every private repository of that owner anyway (RepositoryVisibility). Every failure path narrows to an empty list. " +
        "The detail load (OnLoadAsync) reads nothing at all: it echoes the id the caller sent, so every name 'exists' and " +
        "the page is no oracle for which checks reported where.";

    /// <summary>
    /// The page an exclusion chip links to (<c>/po/forgecheck/&lt;name&gt;</c>, D117): the check named <paramref name="id"/>,
    /// built from the id alone, since a check has no document to load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not calling the base is the point: there is no document for its collection guard and row rule to judge, so the
    /// base answered 404 for every chip. The type-level <c>Read</c> right has been checked by the framework before this
    /// runs. What is returned is only the name the caller supplied, so it discloses nothing and is no existence oracle:
    /// every name loads, whether or not it ever reported. Which repository reported it stays with the manager-gated
    /// queries above.
    /// </para>
    /// <para>
    /// Mapped by the framework's own <see cref="IEntityMapper"/>, the mapper the base load uses, so the object is
    /// scaffolded from <c>ForgeCheck.json</c> for this caller like any loaded one. It is read-only: <c>Name</c> is
    /// read-only in the model, ForgeCheck grants no <c>Edit</c> or <c>Delete</c> (only <c>QueryRead</c>), and
    /// <see cref="PersistentObject.Can"/> says so for the row as well.
    /// </para>
    /// </remarks>
    public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var po = await entityMapper.ToPersistentObjectAsync(new ForgeCheck { Id = id, Name = id });
        po.Breadcrumb = id;
        po.Can = new PersistentObjectPermissions { Edit = false, Delete = false };
        return po;
    }

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
        // The repository's exclusion picker: whoever may edit the repository's settings (R39, D-O12), a repository admin too.
        if (repository is null || !await visibility.CanManageRepositoryAsync(repository))
            return [];

        return DistinctRows(repository.KnownChecks ?? []);
    }

    /// <summary>
    /// One row per name, the most recent sighting's, compared ignoring case: Spark's row ids are case-insensitive (Raven
    /// id semantics) and refuse two rows whose ids differ only in case. Seen locally 2026-10-15: one repository reported
    /// "tests / Other suites", another "tests / other suites", and the account picker failed with a 500.
    /// </summary>
    private static ForgeCheck[] DistinctRows(IEnumerable<ForgeCheck> checks)
        => [.. checks
            .Where(check => !string.IsNullOrEmpty(check?.Id))
            .GroupBy(check => check.Id, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.MaxBy(check => check.LastSeenUtc)!)];

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

        return DistinctRows(repositories.SelectMany(r => r.KnownChecks ?? []));
    }
}
