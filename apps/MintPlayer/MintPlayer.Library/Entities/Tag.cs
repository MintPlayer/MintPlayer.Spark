using System.Drawing;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Entities;

/// <summary>A tag in a <see cref="TagCategory"/>, optionally nested under a parent tag.</summary>
public partial class Tag : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;

    [Reference(typeof(TagCategory))]
    public string? CategoryId { get; set; }

    [Reference(typeof(Tag))]
    public string? ParentId { get; set; }
}

/// <summary>A group of tags (genre, mood, …), with the colour its tags are drawn in.</summary>
public partial class TagCategory : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public Color Color { get; set; }
}
