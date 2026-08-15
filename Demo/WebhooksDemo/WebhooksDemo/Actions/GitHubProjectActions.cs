using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Actions;

public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    /// <summary>
    /// The org-membership rule as an <b>async row filter</b> (#239) — the worked example of the
    /// feature. Building the predicate needs I/O: the caller's allowed owner logins are fetched
    /// live from GitHub (via their stored OAuth token, cached per request), which the old
    /// synchronous <c>GetRowFilter</c> could not express — the class was stuck on
    /// <c>IsAllowedAsync</c> post-materialization filtering, with no pushdown and no create-side
    /// <c>WITH CHECK</c>.
    /// <para>
    /// Now the framework <c>await</c>s the allow-list once per request, pushes
    /// <c>owner in (…)</c> into the RavenDB query on list/query paths, and derives the
    /// detail/edit/delete single-row checks and the create/edit WITH CHECK from the same
    /// expression. So the three hand-written org checks this class used to carry in
    /// <c>OnLoadAsync</c> / <c>OnBeforeSaveAsync</c> / <c>OnBeforeDeleteAsync</c> are gone — the
    /// one filter covers every path.
    /// </para>
    /// </summary>
    public override async Task<Expression<Func<GitHubProject, bool>>?> GetRowFilterAsync(string action)
    {
        var owners = await _orgAccess.GetAllowedOwnersAsync();
        return project => owners.Contains(project.OwnerLogin);
    }

    public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        // Org membership is enforced by GetRowFilterAsync's WITH CHECK now; this hook keeps only
        // its real job: when a project is saved with a NodeId but no columns yet, auto-fetch the
        // Status field and column options from GitHub.
        if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
        {
            var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(entity.InstallationId, entity.NodeId);
            entity.StatusFieldId = statusFieldId;
            entity.Columns = columns;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
