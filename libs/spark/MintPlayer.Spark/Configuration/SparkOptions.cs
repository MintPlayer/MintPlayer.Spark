using System.Reflection;

namespace MintPlayer.Spark.Configuration;

public class SparkOptions
{
    public BreadcrumbOptions Breadcrumb { get; set; } = new();

    /// <summary>
    /// Decides what each property of a context-root type becomes when the synchronizer first creates its
    /// model attribute (<c>docs/default_index_PRD.md</c>, D9): <see cref="ModelAttributeSeed.Default"/>,
    /// <see cref="ModelAttributeSeed.Hidden"/> or <see cref="ModelAttributeSeed.Exclude"/>. It matters most
    /// for library types (an <c>Organizations</c> root over <c>Organization</c>), whose properties the app
    /// cannot annotate.
    /// <para>
    /// Defaults to <see cref="ModelAttributeConventions.AuditMembersHidden"/>. Replace or wrap it with
    /// <c>spark.ModelAttributes(...)</c>. <c>[IgnoreProperty]</c> always excludes, whatever this returns.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Runs only during synchronize, and only when an attribute is first created: it never changes or
    /// deletes an existing attribute (#253). It must be pure — the same answer for the same property on
    /// every run — or the model file churns.
    /// </remarks>
    public Func<Type, PropertyInfo, ModelAttributeSeed> ModelAttribute { get; set; } = ModelAttributeConventions.AuditMembersHidden;
}

public class BreadcrumbOptions
{
    /// <summary>
    /// Maximum reference depth the breadcrumb resolver follows (roots are depth 0). Bounds the
    /// number of batched load round-trips and breaks reference cycles. Default: 5.
    /// </summary>
    public int MaxDepth { get; set; } = 5;

    /// <summary>Separator joining the breadcrumbs of a reference-array placeholder. Default: ", ".</summary>
    public string ReferenceSeparator { get; set; } = ", ";

    /// <summary>
    /// Rendered in place of a referenced entity the current user is not allowed to read
    /// (row-level Read denied). Default: "—".
    /// </summary>
    public string RedactedPlaceholder { get; set; } = "—";
}
