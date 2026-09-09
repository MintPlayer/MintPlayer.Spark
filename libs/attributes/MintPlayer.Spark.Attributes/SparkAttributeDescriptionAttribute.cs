using System.Diagnostics;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Carries a property's <c>///</c> summary into the compiled assembly so <c>--spark-synchronize-model</c>
/// can seed the attribute's English <c>description</c> from it (#348). Emitted by the
/// <c>AttributeDescriptionsGenerator</c>; never written by hand.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConditionalAttribute"/> with <c>DEBUG</c> means the compiler drops every application of
/// this attribute from a Release build of the entity assembly. Descriptions are development-time
/// input to the model JSON, which is the production artefact; nothing here ships. A Release-built
/// entity assembly therefore looks to the synchronizer exactly like one whose properties carry no
/// summaries.
/// </para>
/// <para>
/// Assembly-level rather than property-level because a generator cannot add an attribute to an
/// ordinary property declaration — only to a partial type. The (type, property) pair is the key the
/// synchronizer looks up while reflecting over the entity's properties.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[Conditional("DEBUG")]
public sealed class SparkAttributeDescriptionAttribute(Type type, string property, string summary) : Attribute
{
    /// <summary>The type declaring the property.</summary>
    public Type Type { get; } = type;

    /// <summary>The property name, as declared.</summary>
    public string Property { get; } = property;

    /// <summary>The summary rendered to plain text: paragraphs separated by <c>\n</c>, crefs reduced to their simple name.</summary>
    public string Summary { get; } = summary;
}
