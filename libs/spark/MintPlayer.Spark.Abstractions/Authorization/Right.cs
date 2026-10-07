using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Represents a permission assignment linking a group to a resource.
/// </summary>
public class Right
{
    /// <summary>
    /// The right's stable key in the composed set (composition D4): the application's own key as
    /// written (any text without <c>:</c>, conventionally the id it always had), or
    /// <c>{alias}:{key}</c> for a grant a library ships (<c>authorization:passkeys-read</c>). The
    /// application removes a library grant by this key and never edits it.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The library alias that ships this right, or <see langword="null"/> for the application's own:
    /// the posture table's provenance column. Not part of the file.
    /// </summary>
    [JsonIgnore]
    public string? Layer { get; set; }

    /// <summary>
    /// The group as the layer named it — <c>@anonymous</c>, <c>@authenticated</c>, a slot such as
    /// <c>moderation:moderators</c>, or the id — before it resolved to <see cref="GroupId"/>. Not part
    /// of the file: <c>groupId</c> holds it there.
    /// </summary>
    [JsonIgnore]
    public string? Group { get; set; }

    /// <summary>
    /// The resource this right applies to, as <c>{Action}/{Target}</c>:
    /// <list type="bullet">
    /// <item><c>"Read/Person"</c> — read access to the Person entity</item>
    /// <item><c>"EditNewDelete/Person"</c> — a combined action covering all three</item>
    /// <item><c>"CarCopy/Car"</c> — a custom action defined on the Actions class</item>
    /// <item><c>"QueryRead/Person/Salary"</c> — an attribute-level right, <c>{verb}/{Type}/{Attribute}</c></item>
    /// </list>
    /// Wildcards (<c>*</c>) are refused when the file loads — name every target.
    /// <para>
    /// <b>The attribute-level form</b> (since #465) takes only <c>Query</c>, <c>Read</c>, <c>Edit</c>,
    /// <c>New</c> and the combined verbs made solely of them, and names an attribute the type's model
    /// declares; anything else refuses the file. It composes over the type right and never unlocks
    /// it. A deny is how an attribute is hidden from a group (#264: there is no <c>isVisible</c>);
    /// a deny on both <c>wellKnown</c> groups hides it from everyone.
    /// </para>
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// The ID of the group this right is assigned to, once resolved. Must match a key in
    /// SecurityConfiguration.Groups. In the file, <c>groupId</c> may also be a token
    /// (<c>@anonymous</c>, <c>@authenticated</c>) or a slot the application binds; a slot bound to
    /// several groups composes into one right per group.
    /// </summary>
    public Guid GroupId { get; set; }

    /// <summary>
    /// Denies the resource rather than granting it.
    /// <para>
    /// <b>A denial is absolute unless an important right overrides it.</b> It cannot be re-granted
    /// by adding the caller to another group — every denial is checked before any grant, on the
    /// whole of the caller's group set. So a denial on the <c>authenticated</c> group locks out
    /// administrators too.
    /// </para>
    /// <para>
    /// Denials expand exactly as grants do: <c>EditNewDelete/Car</c> denies Edit, New and Delete.
    /// </para>
    /// </summary>
    public bool IsDenied { get; set; }

    /// <summary>
    /// Wins over everything else, denials included.
    /// <para>
    /// The tier exists so a decision can be made unconditionally rather than by hoping no other
    /// group contributes a contradicting right: an important grant is reachable no matter what
    /// else the file says, and an important denial cannot be granted around. Use it for the small
    /// set of rights where being sure matters more than being composable — a break-glass
    /// administrative grant, or a hard prohibition that must survive any future group.
    /// </para>
    /// <para>
    /// Two important rights that contradict each other resolve to the denial: within the tier the
    /// safer answer wins, because the alternative is to make the outcome depend on file order.
    /// </para>
    /// <para>
    /// This used to be documented as an audit marker ("can be used for enhanced logging"), which
    /// nothing implemented and which describes something else entirely. It is a precedence tier.
    /// </para>
    /// </summary>
    public bool IsImportant { get; set; }
}
