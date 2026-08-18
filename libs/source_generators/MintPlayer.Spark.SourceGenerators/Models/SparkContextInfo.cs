using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// The application's <c>SparkContext</c>, so query roots can be contributed to it — the
/// <c>IRavenQueryable&lt;VCar&gt; VCars =&gt; Session.Query&lt;VCar, Cars_Overview&gt;()</c> members that Fleet
/// and HR write by hand today.
/// </summary>
[AutoValueComparer]
public partial class SparkContextInfo
{
    public string ClassName { get; set; } = string.Empty;

    /// <summary>Namespace and containing-type chain, so a nested context is reopened inside its parents.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>
    /// Whether the context is declared <c>partial</c>. Without it nothing can be contributed and
    /// <c>SPARK_INDEX_008</c> is reported rather than the roots silently not appearing.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Every member name already declared on the context. A hand-written root is a legitimate override, so a
    /// name collision means "emit nothing for this one" rather than a diagnostic — and emitting anyway would
    /// be a duplicate-member compile error.
    /// </summary>
    public List<string> ExistingMemberNames { get; set; } = new();

    public LocationKey? Location { get; set; }
}
