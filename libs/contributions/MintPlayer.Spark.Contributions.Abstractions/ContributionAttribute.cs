namespace MintPlayer.Spark.Contributions;

/// <summary>
/// Marks a property of a persistent-object entity (the <em>target</em>) as a per-user contribution
/// list with latest-wins: every user writes their own contribution document per slot, and the target
/// shows the most recent non-hidden one per slot.
/// </summary>
/// <remarks>
/// <para>
/// The generator derives the owning type, the contribution name (the property name) and the element
/// type from the property; the property may be <c>T</c>, <c>T[]</c>, <c>List&lt;T&gt;</c>,
/// <c>IList&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c> or
/// <c>IEnumerable&lt;T&gt;</c>. The element's slots are its <see cref="ContributionSlotAttribute"/>
/// properties, in declaration order.
/// </para>
/// <para>
/// The generated types are named <c>{Target}{Property}Contribution</c> and
/// <c>{Target}{Property}Current</c> (for <c>Song.Lyrics</c>: <c>SongLyricsContribution</c> and
/// <c>SongLyricsCurrent</c>), with no override. Those names become RavenDB collection names and
/// <c>security.json</c> resources, so renaming the target or the property later means migrating
/// collections and grants.
/// </para>
/// <para>
/// The property must carry Newtonsoft's <c>[JsonIgnore]</c>: its contents are stored in the
/// contribution and current documents, never on the target.
/// </para>
/// <para>
/// ⚠️ The analyzer SPARK017 in <c>MintPlayer.Spark.SourceGenerators</c> and this library's generator
/// match this attribute by its metadata name <c>MintPlayer.Spark.Contributions.ContributionAttribute</c>.
/// Moving it to another namespace silently turns both off.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public partial class Song {
///     [Contribution(Attribution = ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt)]
///     [Newtonsoft.Json.JsonIgnore]
///     public List&lt;Lyrics&gt; Lyrics { get; set; } = new();
/// }
/// public partial class Lyrics {
///     [ContributionSlot] public string Language { get; set; } = "";
///     [ContributionSlot] public string Script { get; set; } = "";
///     public string Text { get; set; } = "";
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ContributionAttribute : Attribute
{
    /// <summary>
    /// What is stored, loaded and shown about who contributed the current version of a row. The
    /// default, <see cref="ContributionAttribution.None"/>, adds nothing.
    /// </summary>
    public ContributionAttribution Attribution { get; set; } = ContributionAttribution.None;
}
