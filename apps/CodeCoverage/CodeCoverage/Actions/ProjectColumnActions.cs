using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
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
/// (<c>clrType</c>-less) query: none of the composed-query costs apply — no
/// <c>ISparkOwnsRowSecurity</c> to implement, no row-filtering or redaction responsibilities
/// transferred to this class. Raven-specific steps in the executor are all guarded on the returned
/// object actually being a Raven queryable, so returning a plain sequence simply skips them, and
/// sorting, search and paging fall back to their in-memory equivalents.
/// </para>
/// </summary>
public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    [Inject] private readonly IAsyncDocumentSession session;

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
