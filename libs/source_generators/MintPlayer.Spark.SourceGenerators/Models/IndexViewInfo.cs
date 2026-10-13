using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// A public <c>[FromIndex]</c> view, hand-written or generated, which gets a typed root on the app's context:
/// <c>VPeople =&gt; Session.Query&lt;VPerson, People_Overview&gt;()</c> (<c>docs/default_index_PRD.md</c>, D6).
/// </summary>
[GenerateEquality]
public partial class IndexViewInfo
{
    /// <summary>Fully-qualified, <c>global::</c>-prefixed view type.</summary>
    public string ViewFullName { get; set; } = string.Empty;

    /// <summary>Fully-qualified, <c>global::</c>-prefixed index type.</summary>
    public string IndexFullName { get; set; } = string.Empty;

    /// <summary>
    /// The root name <c>IndexNaming</c> derives for it (<c>VCar</c> over <c>Car</c> is <c>VCars</c>); the context
    /// step takes the first free name from here (D7).
    /// </summary>
    public string WantedMemberName { get; set; } = string.Empty;
}

/// <summary>
/// One context's contribution, with every root name already resolved (D7): the producer only writes.
/// </summary>
[GenerateEquality]
public partial class ContextRootsPlan
{
    public SparkContextInfo Context { get; set; } = new();

    public List<ResolvedViewRootInfo> ViewRoots { get; set; } = new();
}

/// <summary>Every context's plan, as one value-equal model so the producer step stays cached when nothing changed.</summary>
[GenerateEquality]
public partial class ContextPlanSet
{
    public List<ContextRootsPlan> Plans { get; set; } = new();
}

/// <summary>A view root with its final member name.</summary>
[GenerateEquality]
public partial class ResolvedViewRootInfo
{
    public string ViewFullName { get; set; } = string.Empty;

    public string IndexFullName { get; set; } = string.Empty;

    public string MemberName { get; set; } = string.Empty;

    /// <summary>The name <c>IndexNaming</c> wanted; differs from <see cref="MemberName"/> when it was taken.</summary>
    public string WantedMemberName { get; set; } = string.Empty;
}
