using System.Linq.Expressions;
using MintPlayer.Entities;
using MintPlayer.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Actions;

/// <summary>
/// Playlists belong to the user who made them. Everyone reads the public ones, the owner reads and changes
/// their own, and an administrator everything.
/// </summary>
public partial class PlaylistActions : DefaultPersistentObjectActions<Playlist>, IBeforeSave<Playlist>
{
    [Inject] private readonly MintPlayerAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<Playlist, bool>>?> GetRowFilterAsync(string action)
    {
        if (action == "New" || await access.IsAdministratorAsync())
            return null;

        var userId = access.UserId;
        if (action is "Read" or "Query")
            return userId is null ? p => p.IsPublic : p => p.IsPublic || p.OwnerId == userId;
        return userId is null ? p => false : p => p.OwnerId == userId;
    }

    /// <summary>The creator becomes the owner; an edit never changes the owner.</summary>
    public ValueTask OnBeforeSaveAsync(Playlist entity, SaveContext context)
    {
        entity.OwnerId = context.IsNew ? access.UserId : (context.Before as Playlist)?.OwnerId ?? entity.OwnerId;
        return ValueTask.CompletedTask;
    }

    /// <summary>The caller's own playlists. Source: "Custom.My_Playlists".</summary>
    public Task<IQueryable<Playlist>> My_Playlists(CustomQueryArgs args)
    {
        var userId = access.UserId;
        IQueryable<Playlist> query = userId is null
            ? Enumerable.Empty<Playlist>().AsQueryable()
            : session.Query<Playlist>().Where(p => p.OwnerId == userId);
        return Task.FromResult(query);
    }

    /// <summary>Everyone's public playlists. Source: "Custom.Public_Playlists".</summary>
    public Task<IQueryable<Playlist>> Public_Playlists(CustomQueryArgs args)
        => Task.FromResult<IQueryable<Playlist>>(session.Query<Playlist>().Where(p => p.IsPublic));
}
