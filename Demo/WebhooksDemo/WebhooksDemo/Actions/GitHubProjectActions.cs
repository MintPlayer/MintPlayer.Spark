using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Actions;

public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    public override async Task<IEnumerable<GitHubProject>> OnQueryAsync(IAsyncDocumentSession session)
    {
        var owners = await _orgAccess.GetAllowedOwnersAsync();
        if (owners.Length == 0) return [];

        return await session.Query<GitHubProject>()
            .Where(p => owners.Contains(p.OwnerLogin))
            .ToListAsync();
    }

    public override async Task<GitHubProject?> OnLoadAsync(IAsyncDocumentSession session, string id)
    {
        var project = await session.LoadAsync<GitHubProject>(id);
        if (project is null) return null;

        if (!await _orgAccess.IsOwnerAllowedAsync(project.OwnerLogin))
            throw new SparkAccessDeniedException("Read/GitHubProject");

        return project;
    }

    public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        if (!await _orgAccess.IsOwnerAllowedAsync(entity.OwnerLogin))
            throw new SparkAccessDeniedException("Edit/GitHubProject");

        // When a project is saved with a NodeId but no columns yet,
        // auto-fetch the Status field and column options from GitHub.
        if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
        {
            var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(entity.InstallationId, entity.NodeId);
            entity.StatusFieldId = statusFieldId;
            entity.Columns = columns;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }

    public override async Task OnBeforeDeleteAsync(GitHubProject entity)
    {
        if (!await _orgAccess.IsOwnerAllowedAsync(entity.OwnerLogin))
            throw new SparkAccessDeniedException("Delete/GitHubProject");
    }
}
