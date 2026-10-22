using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Entities;

/// <summary>
/// Shared base of the taggable, media-bearing catalog entities: <see cref="Person"/>, <see cref="Artist"/>
/// and <see cref="Song"/> (legacy <c>Subjects</c> table). Ids keep their legacy numbers (<c>Songs/313</c>),
/// so the legacy URLs redirect without a lookup.
/// </summary>
public abstract partial class Subject : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }

    /// <summary>Links to this subject elsewhere: YouTube, Spotify, Wikipedia, the official site.</summary>
    /// <remarks>Detail pages only, so it is kept out of the list indexes.</remarks>
    [IgnoreForIndex]
    public List<Medium> Media { get; set; } = [];

    [Reference(typeof(Tag))]
    public List<string> TagIds { get; set; } = [];
}
