using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Actions;

public partial class SyncColumnsAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;
    [Inject] private readonly IGitHubProjectService projectService;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var source = args.Parent ?? args.SelectedItems.FirstOrDefault();
        if (source?.Id is null)
            throw new InvalidOperationException("No item selected");

        var project = await dbAccess.GetDocumentAsync<GitHubProject>(source.Id);
        if (project is null)
            throw new InvalidOperationException("Project not found");

        var (statusFieldId, columns) = await projectService.GetProjectColumnsAsync(project.InstallationId, project.NodeId);
        project.StatusFieldId = statusFieldId;
        project.Columns = columns;

        await dbAccess.SaveDocumentAsync(project);
    }
}
