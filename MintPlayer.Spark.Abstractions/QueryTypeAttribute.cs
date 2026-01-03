namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Specifies the type returned by a RavenDB index when querying an entity.
/// When this attribute is applied, list queries will automatically use the specified index
/// instead of querying the collection directly.
/// </summary>
/// <example>
/// <code>
/// [QueryType(typeof(VPerson), IndexName = "People_Overview")]
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
///
/// // The RavenDB index definition
/// public class People_Overview : AbstractIndexCreationTask&lt;Person&gt;
/// {
///     public People_Overview()
///     {
///         Map = people => from person in people
///                         select new VPerson { FullName = person.FirstName + " " + person.LastName };
///     }
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
    /// The name of the RavenDB index to use for list queries.
    /// If not specified, the framework will attempt to use the convention-based name.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// Creates a new QueryTypeAttribute with the specified projection type.
    /// </summary>
    /// <param name="projectionType">The type returned by the RavenDB index.</param>
    public QueryTypeAttribute(Type projectionType)
    {
        ProjectionType = projectionType;
    }
}
