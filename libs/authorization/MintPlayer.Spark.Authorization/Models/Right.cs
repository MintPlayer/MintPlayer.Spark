namespace MintPlayer.Spark.Authorization.Models;

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
    /// </list>
    /// <para>
    /// <b>There is no property-level form.</b> This comment used to advertise
    /// <c>"Edit/Person/Salary"</c>, but matching is exact string equality and nothing in Spark ever
    /// builds a three-segment resource — so such a right would parse, load, and silently never
    /// match anything. Scope a single property through the Actions class instead
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
    /// When true, this explicitly denies the permission.
    /// Denials take precedence over grants.
    /// </summary>
    public bool IsDenied { get; set; }

    /// <summary>
    /// When true, marks this as an important/sensitive permission.
    /// Can be used for enhanced logging or audit purposes.
    /// </summary>
    public bool IsImportant { get; set; }
}
