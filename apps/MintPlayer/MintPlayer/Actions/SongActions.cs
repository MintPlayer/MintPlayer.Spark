using MintPlayer.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Actions;

public partial class SongActions : DefaultPersistentObjectActions<Song>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;

    public string RowSecurityRationale => CatalogRowSecurity.Rationale;

    /// <summary>The songs that credit an artist. Source: "Custom.Artist_Songs".</summary>
    public Task<IQueryable<Song>> Artist_Songs(CustomQueryArgs args)
    {
        args.EnsureParent("Artist");
        var artistId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Song>>(session.Query<Song>().Where(s => s.Artists.Any(a => a.ArtistId == artistId)));
    }
}
