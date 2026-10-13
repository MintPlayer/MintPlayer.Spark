namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Generator-emitted: marks <paramref name="propertyName"/> as the <b>plain</b> entity root of
/// <paramref name="entityType"/> on a <c>SparkContext</c> (<c>docs/default_index_PRD.md</c>, D6). Not written by hand.
/// </summary>
/// <remarks>
/// <para>
/// A context may expose one entity type through several roots (<c>Cars</c>, <c>ArchivedCars</c>). A
/// <c>[Reference(typeof(T))]</c> that names no query resolves to the one whose body is the plain
/// <c>Session.Query&lt;T&gt;()</c>. The synchronizer reflects at runtime and cannot read a getter's body, but the
/// source generator can, so it classifies every root and records the plain ones here, on the context's generated
/// partial. The synchronizer reads the markers off the context type, walking its base types; a base context from a
/// referenced assembly carries its own markers from that assembly's own generator run.
/// </para>
/// <para>
/// Plain means a body that is a single <c>Query&lt;T&gt;()</c> call with no arguments on a RavenDB session, in any of
/// the three body forms (<c>=&gt;</c>, <c>get =&gt;</c>, <c>get { return …; }</c>). A filter, an index or collection
/// argument, <c>.Customize(...)</c> or a helper method all make a root not plain (PRD §7, SP3).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public partial class HRContext : SparkContext
/// {
///     public IRavenQueryable&lt;Person&gt; People =&gt; Session.Query&lt;Person&gt;();
/// }
///
/// // generated:
/// [SparkPlainRoot(typeof(Person), nameof(HRContext.People))]
/// public partial class HRContext { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class SparkPlainRootAttribute : Attribute
{
    /// <param name="entityType">The entity type the root exposes.</param>
    /// <param name="propertyName">The root's property name on the context (or on a base context).</param>
    public SparkPlainRootAttribute(Type entityType, string propertyName)
    {
        EntityType = entityType;
        PropertyName = propertyName;
    }

    /// <summary>The entity type the root exposes.</summary>
    public Type EntityType { get; }

    /// <summary>The root's property name.</summary>
    public string PropertyName { get; }
}
