using System.Reflection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Sets <see cref="SparkOptions.ModelAttribute"/>, the hook deciding what a context-root type's properties
/// become when the synchronizer first creates their attributes (<c>docs/default_index_PRD.md</c>, D9):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark.ModelAttributes((type, property) =>
///     property.Name == "InternalNotes" ? ModelAttributeSeed.Exclude
///         : ModelAttributeConventions.AuditMembersHidden(type, property)));
/// </code>
/// Wrap <see cref="ModelAttributeConventions.AuditMembersHidden"/> to keep the audit convention.
/// </summary>
public static class SparkBuilderModelAttributeExtensions
{
    /// <summary>Replaces <see cref="SparkOptions.ModelAttribute"/>.</summary>
    public static ISparkBuilder ModelAttributes(this ISparkBuilder builder, Func<Type, PropertyInfo, ModelAttributeSeed> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        // ISparkBuilder does not expose the options (they belong to the framework, not the abstractions).
        // The synchronize command reads them back off the SparkOptions singleton that AddSpark registers
        // (PRD §7, SP5), so the hook must land on that same instance — the builder's — not on a copy
        // configured through IOptions<T>.
        if (builder is not SparkBuilder sparkBuilder)
            throw new InvalidOperationException(
                $"{nameof(ModelAttributes)} needs the builder AddSpark creates; got {builder.GetType().FullName}.");
        sparkBuilder.Options.ModelAttribute = seed;
        return builder;
    }
}
