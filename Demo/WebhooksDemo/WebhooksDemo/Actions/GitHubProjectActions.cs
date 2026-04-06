using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;
using MintPlayer.SourceGenerators.Attributes;

namespace WebhooksDemo.Actions;

public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly IGitHubProjectService _projectService;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        // When a project is saved with a NodeId but no columns yet,
        // auto-fetch the Status field and column options from GitHub.
        if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
        {
            var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(entity.NodeId);
            entity.StatusFieldId = statusFieldId;
            entity.Columns = columns;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
