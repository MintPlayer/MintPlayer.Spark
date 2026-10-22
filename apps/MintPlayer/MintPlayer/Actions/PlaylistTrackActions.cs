using MintPlayer.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Actions;

/// <summary>
/// Exists to say why the anonymous grant on <c>PlaylistTrack</c> is safe: the type is an <c>asDetailType</c> only
/// (<see cref="Playlist.Tracks"/>), and its grant buys the nested form's metadata.
/// </summary>
public partial class PlaylistTrackActions : DefaultPersistentObjectActions<PlaylistTrack>, ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "This type declares no query and owns no collection: its rows are the Tracks list embedded in one " +
        "Playlist document, and they reach a caller only as part of that playlist's page. Scoping is therefore " +
        "the playlist's, in PlaylistActions' row filter (public playlists, the owner's own, or an administrator), " +
        "and writes go through the playlist's save, which that filter narrows to the owner. The grant here is " +
        "for the nested form's metadata, which carries no row data at all.";
}
