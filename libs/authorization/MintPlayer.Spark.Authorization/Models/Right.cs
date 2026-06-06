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
    /// The resource this right applies to.
    /// Format examples:
    /// - "Read/DemoApp.Person" - Read access to Person entity
    /// - "Edit/DemoApp.Person/Salary" - Edit access to Salary property on Person
    /// - "EditNewDelete/DemoApp.Person" - Combined Edit, New, and Delete access
    /// - "Execute/GetActiveEmployees" - Execute permission for a query
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
