namespace Iconic.Entities;

/// <summary>
/// A project's drawing: points, constraints, layers, shapes, outputs and timeline, as the editor's JSON
/// (docs/iconic_PRD.md §3, D10). One document per project, <c>ProjectScenes/{key}</c> beside <c>Projects/{key}</c>.
/// It is not in the Spark model: only the editor's endpoints read and write it, so it is never edited field by field.
/// </summary>
public class ProjectScene
{
    public string? Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string? OwnerId { get; set; }

    /// <summary>Raised by every write; a write names the version it started from, so two tabs never overwrite each other.</summary>
    public long Version { get; set; }

    /// <summary>The scene as JSON text. A patch parses it, applies the operations and writes it back (S5).</summary>
    public string Json { get; set; } = "{}";

    /// <summary>The length of <see cref="Json"/>, against the 5 MB quota (D21).</summary>
    public long Bytes { get; set; }
}
