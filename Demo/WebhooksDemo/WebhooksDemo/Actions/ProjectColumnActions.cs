using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using WebhooksDemo.Entities;

namespace WebhooksDemo.Actions;

public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    public async Task<IEnumerable<ProjectColumn>> GetProjectColumns(CustomQueryArgs args)
    {
        if (args.Parent is null)
            return [];

        var project = await args.Session.LoadAsync<GitHubProject>(args.Parent.Id);
        return project?.Columns ?? [];
    }
}
