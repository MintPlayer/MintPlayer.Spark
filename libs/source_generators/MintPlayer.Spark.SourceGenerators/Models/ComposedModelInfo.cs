using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One <c>Model/*.json</c> a referenced library ships (<c>[assembly: SparkLayer]</c>), as an incremental input.</summary>
[GenerateEquality]
public partial class LibraryModelFileInfo
{
    public string Assembly { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>The text composed: with the application's renames applied (D28).</summary>
    public string Json { get; set; } = string.Empty;
    public List<string> DependsOn { get; set; } = new();

    /// <summary>The library's <c>SparkLibraryAlias</c>: with <see cref="DeclaredName"/>, the target of an application's <c>$extends</c>.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>The name the file declares before renames; <see langword="null"/> when it states none (the composer reports that).</summary>
    public string? DeclaredName { get; set; }

    /// <summary>The text as the library shipped it.</summary>
    public string ShippedJson { get; set; } = string.Empty;
}

/// <summary>One of the application's own <c>Model/*.json</c> AdditionalFiles.</summary>
[GenerateEquality]
public partial class AppModelFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
