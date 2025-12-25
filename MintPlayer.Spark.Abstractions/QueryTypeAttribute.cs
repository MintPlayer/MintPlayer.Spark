namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Specifies the type returned by a RavenDB index when querying an entity.
/// This enables the framework to know which projection type to use when querying via the index.
/// </summary>
/// <example>
/// <code>
/// [QueryType(typeof(VPerson))]
/// public class Person
/// {
///     public string FirstName { get; set; }
///     public string LastName { get; set; }
/// }
///
/// // The projection class used by the index
/// public class VPerson
/// {
///     public string FullName { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class QueryTypeAttribute : Attribute
{
    /// <summary>
    /// The type returned by the RavenDB index projection.
    /// </summary>
    public Type ProjectionType { get; }

    /// <summary>
    /// Creates a new QueryTypeAttribute with the specified projection type.
    /// </summary>
    /// <param name="projectionType">The type returned by the RavenDB index.</param>
    public QueryTypeAttribute(Type projectionType)
    {
        ProjectionType = projectionType;
    }
}
