using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Entities;

/// <summary>
/// An ordered list of songs owned by one user. The owner reads and edits it; everyone reads it when
/// <see cref="IsPublic"/> is set (<c>PlaylistActions</c>).
/// </summary>
public partial class Playlist : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>The owner's <c>SparkUser</c> id, stamped on create and never edited.</summary>
    public string? OwnerId { get; set; }

    /// <summary>The tracks; their order is the array position.</summary>
    [Sortable]
    public List<PlaylistTrack> Tracks { get; set; } = [];
}

/// <summary>One track of a <see cref="Playlist"/> (legacy <c>PlaylistSong</c>).</summary>
[ValueObject]
public partial class PlaylistTrack
{
    [Reference(typeof(Song))]
    public string? SongId { get; set; }
}

/// <summary>
/// One user's likes and dislikes, one document per user (<c>UserLikes/{userId}</c>), so toggling is a
/// single-document write. Per-subject totals come from the <c>Likes_Count</c> index.
/// </summary>
public class UserLike
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public List<string> Likes { get; set; } = [];
    public List<string> Dislikes { get; set; } = [];
}
