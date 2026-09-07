using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// Supplies the column options offered when picking a rule's target column.
/// <para>
/// This is what makes <c>EventColumnMapping.TargetColumnOptionId</c> a dropdown rather than a
/// free-text box for an opaque GitHub option id — and it works without promoting columns to
/// documents. <see cref="ProjectColumn"/> stays embedded in <see cref="GitHubProject"/>; the query
/// below simply returns the parent board's own cached columns in memory.
/// </para>
/// <para>
/// The mechanism is <c>[Reference(typeof(ProjectColumn), "Project_Columns")]</c> plus a
/// <c>Custom.*</c> query that reads <see cref="CustomQueryArgs.Parent"/>. Spark resolves and
/// authorizes that parent in the query endpoint, and the client sends the <b>root</b> document as
/// the parent even for a reference on an <c>AsDetail</c> row — which is precisely what a per-board
/// option list needs and what neither lookup shape can express:
/// <see cref="MintPlayer.Spark.Abstractions.TransientLookupReference{TKey}"/> is a static
/// compile-time set, and <c>DynamicLookupReference</c> is one global set per lookup name.
/// </para>
/// <para>
/// The query is <b>entity-backed but not document-backed</b>, and that distinction is what keeps it
/// cheap. <c>ProjectColumn.json</c> declares a <c>clrType</c>, so this is not a "composed"
/// (<c>clrType</c>-less) query: the composed-query limits do not apply, and per-row <c>can</c>,
/// row filtering and redaction are available in principle. Raven-specific steps in the executor are
/// all guarded on the returned object actually being a Raven queryable, so returning a plain sequence simply skips them, and
/// sorting, search and paging fall back to their in-memory equivalents.
/// </para>
/// </summary>
public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <inheritdoc />
    /// <remarks>
    /// Written because the startup gate refused to boot without it, and the refusal was right: the
    /// original grant of <c>QueryRead/ProjectColumn</c> to <c>authenticated</c> plus no row rule on
    /// this class is indistinguishable, to any reviewer or test, from having forgotten to scope a
    /// collection at all.
    /// <para>
    /// A row filter would be the wrong instrument here rather than a missing one. <c>ProjectColumn</c>
    /// has no Raven collection — the rows are a list embedded in one board document — so there is no
    /// queryable for an <c>Expression&lt;Func&lt;ProjectColumn,bool&gt;&gt;</c> to narrow, and
    /// <c>GetRowFilterAsync</c> could not express the scope in any case: its signature takes an
    /// action name and carries no parent.
    /// </para>
    /// </remarks>
    public string RowSecurityRationale =>
        "Rows are never served from a collection: the only query, Project_Columns below, returns the " +
        "Columns embedded in a single parent board, and returns empty unless CustomQueryArgs.Parent is " +
        "present and is a GitHubProject. Scoping therefore lives in the parent's authorization, not " +
        "here — Endpoints/Queries/Execute.cs resolves parentId through " +
        "DatabaseAccess.GetPersistentObjectAsync, which enforces the Read right and runs " +
        "GitHubProjectActions.OnLoadAsync, so GitHubProjectVisibility's row filter applies; a board the " +
        "caller may not see resolves to null and the endpoint answers 404 before this method runs. The " +
        "raw session.LoadAsync below bypasses row security and is safe only for that reason: it reloads " +
        "the id the endpoint already authorized. Every failure path narrows — no parent, wrong parent " +
        "type, or a board that no longer loads all yield an empty option list.";

    /// <summary>
    /// The columns of the board this rule belongs to.
    /// </summary>
    /// <remarks>
    /// Returns empty rather than calling <c>args.EnsureParent(...)</c>, deliberately. The create
    /// form sends no <c>parentId</c>, so <c>EnsureParent</c> would throw and surface as a 500 from
    /// the picker instead of an empty dropdown. Empty is the honest degradation — and boards are
    /// discovered by the reconciler rather than created by hand, so the create form is not on the
    /// real path at all (<c>New/GitHubProject</c> is deliberately not granted).
    /// <para>
    /// The parent type is checked as well as its presence: this query is only meaningful under a
    /// board, and answering with some other parent's columns would be worse than answering with
    /// none.
    /// </para>
    /// <para>
    /// Each returned row's id is <see cref="ProjectColumn.Id"/> — the GitHub single-select option
    /// id — because the mapper reads the CLR <c>Id</c> property directly rather than asking RavenDB
    /// for a document id. That is exactly the value a rule must store, so the picker writes the
    /// right thing with no translation. The stored value is persisted verbatim (a string-typed
    /// reference is never resolved to an entity on save), and on read the breadcrumb lookup misses
    /// harmlessly — the client falls back to matching the value against this option list, which is
    /// the board's complete column set, so the column *name* still renders.
    /// </para>
    /// </remarks>
    public async Task<IEnumerable<ProjectColumn>> Project_Columns(CustomQueryArgs args)
    {
        if (args.Parent is null
            || !string.Equals(args.ParentType, nameof(GitHubProject), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var board = await session.LoadAsync<GitHubProject>(args.Parent.Id);
        return board?.Columns ?? [];
    }
}
