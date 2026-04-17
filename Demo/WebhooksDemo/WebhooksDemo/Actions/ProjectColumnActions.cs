using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Actions;

public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    public async Task<IEnumerable<ProjectColumn>> GetProjectColumns(CustomQueryArgs args)
    {
        if (args.Parent is null)
            return [];

        var project = await args.Session.LoadAsync<GitHubProject>(args.Parent.Id);
        if (project is null) return [];

        if (!await _orgAccess.IsOwnerAllowedAsync(project.OwnerLogin))
            return [];

        return project.Columns;
    }
}
