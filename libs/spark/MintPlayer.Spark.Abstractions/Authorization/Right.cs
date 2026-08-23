namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Represents a permission assignment linking a group to a resource.
/// </summary>
public class Right
{
    /// <summary>
    /// Unique identifier for this right assignment.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The resource this right applies to, as <c>{Action}/{Target}</c>:
    /// <list type="bullet">
    /// <item><c>"Read/Person"</c> — read access to the Person entity</item>
    /// <item><c>"EditNewDelete/Person"</c> — a combined action covering all three</item>
    /// <item><c>"CarCopy/Car"</c> — a custom action defined on the Actions class</item>
    /// <item><c>"Read/*"</c>, <c>"*/Person"</c>, <c>"*/*"</c> — wildcards on either half</item>
    /// </list>
    /// <para>
    /// <b>There is no property-level form.</b> This comment used to advertise
    /// <c>"Edit/Person/Salary"</c>, but matching is per-half and nothing in Spark ever builds a
    /// three-segment resource — so such a right would parse, load, and silently never match
    /// anything. Scope a single property through the Actions class instead
    /// (<c>OnBeforeSaveAsync</c> to reject the change, or omit the attribute from the model).
    /// </para>
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// The ID of the group this right is assigned to.
    /// Must match a key in SecurityConfiguration.Groups.
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
