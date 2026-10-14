using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// The application's <c>SparkContext</c>, so query roots can be contributed to it — the
/// <c>IRavenQueryable&lt;VCar&gt; VCars =&gt; Session.Query&lt;VCar, Cars_Overview&gt;()</c> members that Fleet
/// and HR write by hand today — and its plain entity roots marked (<c>docs/default_index_PRD.md</c>, D6).
/// </summary>
[GenerateEquality]
public partial class SparkContextInfo
{
    public string ClassName { get; set; } = string.Empty;

    /// <summary>Fully-qualified, <c>global::</c>-prefixed, for <c>nameof(Context.Root)</c> in the plain-root marker.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>Namespace and containing-type chain, so a nested context is reopened inside its parents.</summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>
    /// Whether the context, and every type containing it, is declared <c>partial</c>. Without it nothing can be
    /// contributed and <c>SPARK_INDEX_008</c> is reported rather than the roots silently not appearing.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Every name a generated member would collide with or hide (D7): the context's own members and the
    /// non-private members of its base types, plus the context's own name (a member may not share it, CS0542).
    /// <para>⚠️ <c>GetMembers()</c> alone misses inherited members (PRD §7, SP2), which is how a generated root used
    /// to hide a base context's member of the same name.</para>
    /// </summary>
    public List<string> TakenMemberNames { get; set; } = new();

    /// <summary>
    /// The <c>T</c> of every <c>IRavenQueryable&lt;T&gt;</c> property the context already has, own or inherited,
    /// <c>global::</c>-prefixed. A view already exposed (by hand, or by a base context's own generator run) gets no
    /// second root.
    /// </summary>
    public List<string> QueryableTypeFullNames { get; set; } = new();

    /// <summary>The plain entity roots (SP3), own and inherited from base contexts in this compilation.</summary>
    public List<PlainRootInfo> PlainRoots { get; set; } = new();

    public LocationKey? Location { get; set; }
}

/// <summary>One plain entity root: a property whose body is a bare <c>Session.Query&lt;T&gt;()</c> (D6, SP3).</summary>
[GenerateEquality]
public partial class PlainRootInfo
{
    /// <summary>The entity type, <c>global::</c>-prefixed.</summary>
    public string EntityFullName { get; set; } = string.Empty;

    public string PropertyName { get; set; } = string.Empty;
}
