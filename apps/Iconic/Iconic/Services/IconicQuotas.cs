namespace Iconic.Services;

/// <summary>
/// The limits of docs/iconic_PRD.md D21, enforced on the server. The editor reads them from the project response,
/// so its usage meter and the server never disagree.
/// </summary>
public sealed class IconicQuotas
{
    /// <summary>Projects per user.</summary>
    public int MaxProjects { get; set; } = 200;

    /// <summary>Scene JSON per project, in characters: RavenDB's huge-document threshold (5 MB), so it never warns.</summary>
    public long MaxSceneBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Paint layers, fonts, thumbnail and snapshots per project.</summary>
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Named snapshots per project (D22).</summary>
    public int MaxSnapshots { get; set; } = 20;

    /// <summary>Scene saves per user per minute.</summary>
    public int SavesPerMinute { get; set; } = 50;
}
