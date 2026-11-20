using System.Linq.Expressions;
using Iconic.Entities;
using Iconic.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace Iconic.Actions;

/// <summary>
/// A project belongs to the user who made it. Only the owner reads, renames and deletes it through Spark, and an
/// administrator sees every one. The editor creates projects through <c>/api/iconic</c>, so Spark's New is off
/// (security.json). Shared projects are read through their link, never through the Spark model (D20).
/// </summary>
public partial class ProjectActions : DefaultPersistentObjectActions<Project>, IAfterDelete<Project>
{
    [Inject] private readonly IconicAccess access;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ProjectService projects;

    public override async Task<Expression<Func<Project, bool>>?> GetRowFilterAsync(string action)
    {
        if (await access.IsAdministratorAsync()) return null;
        var userId = access.UserId;
        return userId is null ? p => false : p => p.OwnerId == userId;
    }

    /// <summary>The scene document lives beside the project; it goes with it.</summary>
    public async ValueTask OnAfterDeleteAsync(Project entity, DeleteContext context)
        => await projects.DeleteSceneAsync(context.Id, CancellationToken.None);

    /// <summary>The caller's own projects, newest change first. Source: "Custom.My_Projects".</summary>
    public Task<IQueryable<Project>> My_Projects(CustomQueryArgs args)
    {
        var userId = access.UserId;
        IQueryable<Project> query = userId is null
            ? Enumerable.Empty<Project>().AsQueryable()
            : session.Query<Project>().Where(p => p.OwnerId == userId);
        return Task.FromResult(query);
    }
}
