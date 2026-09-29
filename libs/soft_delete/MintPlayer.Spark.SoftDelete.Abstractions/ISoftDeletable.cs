namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// An entity whose delete is a soft delete: the document stays, marked deleted, hidden from every
/// read path, restorable by holders of <c>Restore/T</c> and permanently removable (revisions
/// included) by holders of <c>Purge/T</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implement all four members as <b>public</b> properties — never explicitly. The soft-delete filter
/// is rebound onto the entity by member name (<c>IsDeleted</c>), and RavenDB stores only public
/// properties: an explicit implementation would be stored nowhere, so every row would look live.
/// <c>AddSoftDelete()</c> refuses startup for a model type that gets this wrong.
/// </para>
/// <para>
/// The framework owns these fields. A delete sets them, a restore clears them, and an ordinary edit
/// or create cannot change them (a submitted value is replaced by the stored one).
/// </para>
/// <para>
/// ⚠️ In a hand-written query or index, filter with <c>x.IsDeleted != true</c> — never
/// <c>!x.IsDeleted</c> or <c>x.IsDeleted == false</c>, which silently drop every document stored
/// before the field existed (an absent field matches neither; measured, #460 spike S3). Analyzer
/// SPARK022 flags the wrong shapes.
/// </para>
/// </remarks>
public interface ISoftDeletable
{
    /// <summary>Whether the entity is soft-deleted.</summary>
    bool IsDeleted { get; set; }

    /// <summary>When it was deleted (UTC offset), or null while live.</summary>
    DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The id of the user who deleted it (an id, never a name — #460, D8), or null.</summary>
    string? DeletedBy { get; set; }

    /// <summary>Why it was deleted, when the deleting code gave a reason.</summary>
    string? DeleteReason { get; set; }
}
