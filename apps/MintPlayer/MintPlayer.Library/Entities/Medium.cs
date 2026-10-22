using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Entities;

/// <summary>
/// A link to a subject on another platform. The URL comes first, so it is the leading grid column, where
/// the player renderer shows a play button for URLs the video player can play.
/// </summary>
[ValueObject]
public partial class Medium
{
    public string Value { get; set; } = string.Empty;

    [Reference(typeof(MediumType))]
    public string? TypeId { get; set; }
}

/// <summary>The kind of a <see cref="Medium"/>: YouTube, Spotify, official website, …</summary>
public partial class MediumType : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Whether media of this type are shown to visitors. Hidden types (legacy 15, the lyrics-source links)
    /// stay as data and are shown to editors only.
    /// </summary>
    public bool Visible { get; set; } = true;
}
