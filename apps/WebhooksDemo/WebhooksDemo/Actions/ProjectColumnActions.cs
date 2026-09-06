using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.Services;

namespace WebhooksDemo.Actions;

public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Columns are the shape of a GitHub project board, not its contents, and the board itself is already gated by GitHubProjectActions.GetRowFilterAsync. A caller who cannot see any project sees columns belonging to projects they cannot open, which is board metadata rather than data.";

    [Inject] private readonly IOrganizationAccessService _orgAccess;
    [Inject] private readonly IAsyncDocumentSession _session;

    public async Task<IEnumerable<ProjectColumn>> GetProjectColumns(CustomQueryArgs args)
    {
        if (args.Parent is null)
            return [];

        var project = await _session.LoadAsync<GitHubProject>(args.Parent.Id);
        if (project is null) return [];

        if (!await _orgAccess.IsOwnerAllowedAsync(project.OwnerLogin))
            return [];

        return project.Columns;
    }
}
