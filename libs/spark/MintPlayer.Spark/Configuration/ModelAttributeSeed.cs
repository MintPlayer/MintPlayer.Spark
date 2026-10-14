using System.Reflection;

namespace MintPlayer.Spark.Configuration;

/// <summary>
/// What a CLR property becomes when the synchronizer first creates its model attribute
/// (<c>docs/default_index_PRD.md</c>, D9). Returned by <see cref="SparkOptions.ModelAttribute"/>.
/// </summary>
public enum ModelAttributeSeed
{
    /// <summary>An ordinary attribute, derived from the property as usual.</summary>
    Default,

    /// <summary>
    /// <c>showedOn: "None"</c> plus read-only. ⚠️ Layout only: the value still travels to the client, so
    /// never use this to hide a secret — <see cref="Exclude"/> it, or give the type no context root.
    /// </summary>
    Hidden,

    /// <summary>No attribute is created for the property.</summary>
    Exclude,
}

/// <summary>Spark's default <see cref="SparkOptions.ModelAttribute"/>: the audit-member name convention.</summary>
public static class ModelAttributeConventions
{
    /// <summary>
    /// <c>CreatedBy</c>, <c>CreatedAt*</c>, <c>ModifiedBy</c> and <c>ModifiedAt*</c> become
    /// <see cref="ModelAttributeSeed.Hidden"/>; everything else is <see cref="ModelAttributeSeed.Default"/>.
    /// </summary>
    /// <remarks>
    /// A name convention rather than a check for <c>IAuditable</c>: a library type such as
    /// <c>Organization</c> carries <c>CreatedBy</c>/<c>CreatedAtUtc</c> without implementing the History
    /// package's interfaces, and Spark core does not reference that package. The prefix match covers the
    /// <c>CreatedAtUtc</c> spelling. Pure, as the hook must be.
    /// </remarks>
    public static ModelAttributeSeed AuditMembersHidden(Type entityType, PropertyInfo property)
    {
        var name = property.Name;
        return name is "CreatedBy" or "ModifiedBy"
               || name.StartsWith("CreatedAt", StringComparison.Ordinal)
               || name.StartsWith("ModifiedAt", StringComparison.Ordinal)
            ? ModelAttributeSeed.Hidden
            : ModelAttributeSeed.Default;
    }
}
