using MintPlayer.Entities;
using MintPlayer.Media;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Actions;

public partial class SongActions : DefaultPersistentObjectActions<Song>, IBeforeSave<Song>, ISparkOwnsRowSecurity
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageOutbox outbox;

    public string RowSecurityRationale => CatalogRowSecurity.Rationale;

    /// <summary>The songs that credit an artist. Source: "Custom.Artist_Songs".</summary>
    public Task<IQueryable<Song>> Artist_Songs(CustomQueryArgs args)
    {
        args.EnsureParent("Artist");
        var artistId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Song>>(session.Query<Song>().Where(s => s.Artists.Any(a => a.ArtistId == artistId)));
    }

    /// <summary>
    /// The song page's header (#601 D15/D16): fills the model-only attributes <c>Thumbnail</c> (also the page's
    /// og:image, D18) and <c>ThumbnailMedium</c> (the URL its play button queues) from the cached thumbnails.
    /// The song and its medium types are already in the session from the base load (its default includes);
    /// the thumbnails cost one batched load.
    /// </summary>
    /// <remarks>Overriding this turns off batched loads of songs (<c>SupportsBatchedLoad</c>), so each song is loaded with its header.</remarks>
    public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var result = await base.OnLoadAsync(id, parent);
        if (result is null
            || !result.TryGetAttribute(ThumbnailAttribute, out var thumbnail)
            || !result.TryGetAttribute(ThumbnailMediumAttribute, out var thumbnailMedium))
            return result;

        var song = await session.LoadAsync<Song>(id);
        if (song is null) return result;

        var thumbnailIds = SongHero.CandidateIds(song.Media).ToArray();
        if (thumbnailIds.Length == 0) return result;
        var thumbnails = await session.LoadAsync<MediaThumbnail>(thumbnailIds);
        var typeIds = song.Media.Select(m => m.TypeId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var types = typeIds.Length == 0 ? new Dictionary<string, MediumType>() : await session.LoadAsync<MediumType>(typeIds);

        if (SongHero.Pick(song.Media, types, thumbnails) is { } hero)
        {
            thumbnail.Value = hero.Thumbnail;
            thumbnailMedium.Value = hero.MediumUrl;
        }
        return result;
    }

    /// <summary>The model-only attributes of <c>Song.json</c> the header reads; no property on <see cref="Song"/> (#253).</summary>
    internal const string ThumbnailAttribute = "Thumbnail";
    internal const string ThumbnailMediumAttribute = "ThumbnailMedium";

    /// <summary>
    /// Queues the thumbnails of the media this save adds (#601 D11). The message is stored in the save's own
    /// session, so it commits with the song and is dropped with a refused save.
    /// </summary>
    public async ValueTask OnBeforeSaveAsync(Song entity, SaveContext context)
    {
        var added = AddedMediaUrls(context.Before as Song, entity);
        if (added.Length > 0)
            await outbox.EnqueueAsync(context.GetSession(), new MediaUrlsAdded(added));
    }

    /// <summary>The media URLs <paramref name="after"/> has and <paramref name="before"/> had not, that a player plugin recognises.</summary>
    public static string[] AddedMediaUrls(Song? before, Song after)
    {
        var known = before?.Media.Select(m => m.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        return after.Media
            .Select(m => m.Value)
            .Where(url => !known.Contains(url) && MediaUrl.TryParse(url) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
