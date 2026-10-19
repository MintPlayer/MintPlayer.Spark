using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// One <c>[SparkIndexMap]</c> declaration (commerce X19), from this compilation or a referenced library. Strings
/// and bools only, so the pipeline compares it by value.
/// </summary>
[GenerateEquality]
public partial class DeclaredIndexMapInfo
{
    /// <summary>The index class name the map contributes to, e.g. <c>RefLocalities_Typeahead</c>.</summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>The declaring member for messages, e.g. <c>Lib.TypeaheadIndexes.Places</c>.</summary>
    public string MemberDisplay { get; set; } = string.Empty;

    /// <summary>The member as the generated code reads it, <c>global::</c>-qualified.</summary>
    public string MemberAccess { get; set; } = string.Empty;

    /// <summary>The mapped document type, <c>global::</c>-qualified.</summary>
    public string SourceTypeFullName { get; set; } = string.Empty;

    /// <summary>The map's result type, the index's <c>TReduceResult</c>, <c>global::</c>-qualified.</summary>
    public string ResultTypeFullName { get; set; } = string.Empty;

    /// <summary>Whether the result type marks a property <c>[Fuzzy]</c> (commerce X18): the index then runs Lucene.</summary>
    public bool UsesLucene { get; set; }

    /// <summary>Result-type properties marked <c>[Search]</c>, declared <c>FieldIndexing.Search</c>. Ordinal order.</summary>
    public List<string> SearchFields { get; set; } = new();

    /// <summary><c>StoreAllFields</c> on the attribute.</summary>
    public bool StoreAllFields { get; set; }

    /// <summary>Why the declaration cannot be emitted, or <see langword="null"/> when it can. SPARK_INDEX_015.</summary>
    public string? Error { get; set; }

    /// <summary>Where to anchor diagnostics; <see langword="null"/> for a declaration from a referenced assembly.</summary>
    public LocationKey? Location { get; set; }
}

/// <summary>
/// Every <c>[SparkIndexMap]</c> declaration in the referenced assemblies. A single value-equal model, like
/// <see cref="ReferencedIndexScan"/>: the walk has no syntax to be incremental over.
/// </summary>
[GenerateEquality]
public partial class ReferencedIndexMapScan
{
    public List<DeclaredIndexMapInfo> Maps { get; set; } = new();
}
