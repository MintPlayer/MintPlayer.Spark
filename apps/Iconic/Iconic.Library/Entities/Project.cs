using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;

namespace Iconic.Entities;

/// <summary>What a project exports by default; the editor offers every format for every kind.</summary>
public enum ProjectKind
{
    Icon,
    Cursor,
    Image,
    Animation,
}

/// <summary>
/// One drawing a user made (docs/iconic_PRD.md §3). This document holds what the generic Spark pages show; the
/// drawing itself is the <see cref="ProjectScene"/> beside it, which only the editor's endpoints read and write
/// (D10). Paint-layer bitmaps, fonts, the thumbnail and snapshots are RavenDB attachments on this document.
/// </summary>
public partial class Project : IAuditable
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProjectKind Kind { get; set; } = ProjectKind.Icon;

    /// <summary>The design size in design pixels (D16): 256×256 for icons and cursors by default.</summary>
    public int Width { get; set; } = 256;
    public int Height { get; set; } = 256;

    /// <summary>The owner's <c>SparkUser</c> id, stamped on create and never edited.</summary>
    public string? OwnerId { get; set; }

    /// <summary>The scene's size in bytes, against the 5 MB quota (D21).</summary>
    public long SceneBytes { get; set; }

    /// <summary>All attachments together (paint layers, fonts, thumbnail, snapshots), against the 10 MB quota.</summary>
    public long AttachmentBytes { get; set; }

    /// <summary>Anyone with the share link may view and remix the project (D20).</summary>
    public bool IsPublic { get; set; }

    /// <summary>Listed in the public gallery, a later stage (D20). Kept apart from <see cref="IsPublic"/> from day one.</summary>
    public bool IsListed { get; set; }

    /// <summary>The share link's unguessable token; null until the project is first shared.</summary>
    public string? ShareToken { get; set; }

    /// <summary>Set by an administrator's takedown: the project can no longer be shared.</summary>
    public bool ShareTakenDown { get; set; }

    /// <summary>The named versions (D22), newest last. The scene of each is the attachment <c>snapshots/{Id}.json</c>.</summary>
    public List<ProjectSnapshot> Snapshots { get; set; } = [];
}

/// <summary>A named version of a project's scene (D22).</summary>
[ValueObject]
public partial class ProjectSnapshot
{
    [ValueKey]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long Bytes { get; set; }
}
