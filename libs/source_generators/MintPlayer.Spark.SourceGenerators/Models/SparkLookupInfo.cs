using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One <c>[SparkLookup]</c> enum: what <c>SparkLookupGenerator</c> emits a lookup class for.</summary>
[GenerateEquality]
public partial class SparkLookupInfo
{
    /// <summary>The enum's namespace, empty for the global namespace.</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>The enum's simple name: the middle segment of every key, <c>lookup.{EnumName}.{Member}</c>.</summary>
    public string EnumName { get; set; } = string.Empty;

    /// <summary>The enum, fully qualified with <c>global::</c>.</summary>
    public string EnumFullName { get; set; } = string.Empty;

    /// <summary>The generated class's name, which is also the lookup's name.</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary><c>public</c> or <c>internal</c>, following the enum.</summary>
    public string Accessibility { get; set; } = "public";

    /// <summary>The <c>ELookupDisplayType</c> member name.</summary>
    public string DisplayType { get; set; } = "Dropdown";

    public bool StringKeys { get; set; }

    public List<SparkLookupMemberInfo> Members { get; set; } = new();

    /// <summary>Why no class can be emitted (reported as SPARK090), or null.</summary>
    public string? Error { get; set; }

    public LocationKey? Location { get; set; }
}

[GenerateEquality]
public partial class SparkLookupMemberInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The stored key of a string-keyed lookup: <c>[SparkLookupKey]</c>, else the member's name.</summary>
    public string StringKey { get; set; } = string.Empty;

    /// <summary><c>[SparkLookupOrder]</c>, else 0: items are sorted by it, ties in declaration order.</summary>
    public int Order { get; set; }
}
