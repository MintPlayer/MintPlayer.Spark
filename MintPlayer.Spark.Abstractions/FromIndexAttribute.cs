namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Specifies that this class is a projection type produced by a RavenDB index.
/// The framework uses this to derive the collection type from the index's generic parameter
/// and automatically link queries to use this projection type.
/// </summary>
/// <example>
/// <code>
/// // The projection class used by the index
/// [FromIndex(typeof(People_Overview))]
/// public class VPerson
/// {
///     public string FullName { get; set; }
/// }
///
/// // The RavenDB index definition - framework derives collection type (Person) from generic parameter
/// public class People_Overview : AbstractIndexCreationTask&lt;Person&gt;
/// {
///     public People_Overview()
///     {
///         Map = people => from person in people
///                         select new VPerson { FullName = person.FirstName + " " + person.LastName };
///     }
/// }
///
/// // The entity class - no attributes needed, can live in a separate library project
/// public class Person
/// {
///     public string FirstName { get; set; }
///     public string LastName { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class FromIndexAttribute : Attribute
{
    /// <summary>
    /// The RavenDB index type that produces this projection.
    /// Must derive from AbstractIndexCreationTask&lt;TEntity&gt;.
    /// </summary>
    public Type IndexType { get; }

    /// <summary>
    /// Creates a new FromIndexAttribute with the specified index type.
    /// </summary>
    /// <param name="indexType">The RavenDB index type that produces this projection.</param>
    public FromIndexAttribute(Type indexType)
    {
        IndexType = indexType;
    }
}
